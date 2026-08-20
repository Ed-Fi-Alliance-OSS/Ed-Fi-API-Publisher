// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Web;
using EdFi.Tools.ApiPublisher.Connections.Api.Configuration;
using EdFi.Tools.ApiPublisher.Core.Extensions;
using EdFi.Tools.ApiPublisher.Core.Processing;
using Newtonsoft.Json.Linq;
using Serilog;
using Serilog.Events;

namespace EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement
{
    public class EdFiApiClient : IDisposable, IBearerTokenProvider
    {
        private readonly string _name;
        private readonly ILogger _logger = Log.ForContext(typeof(EdFiApiClient));

        private readonly HttpClient _httpClient;
        private readonly Timer _bearerTokenRefreshTimer;
        private readonly HttpClient _tokenRefreshHttpClient;
        private readonly TimeSpan _configuredRefreshInterval;
        private readonly SemaphoreSlim _tokenRefreshLock = new(1, 1);

        private readonly Lazy<string> _dataManagementApiSegment;
        private readonly Lazy<string> _changeQueriesApiSegment;

        private volatile string _bearerToken;
        private volatile bool _authenticationFailed;
        private TimeSpan _refreshInterval;
        private int _consecutiveTokenFailures;

        // Ticks of the UTC instant at which the current token expires, or 0 when the API reports no lifetime.
        private long _tokenExpiresAtUtcTicks;

        public EdFiApiClient(
            string name,
            ApiConnectionDetails apiConnectionDetails,
            int bearerTokenRefreshMinutes,
            bool ignoreSslErrors,
            HttpClientHandler httpClientHandler = null
        )
        {
            ConnectionDetails =
                apiConnectionDetails ?? throw new ArgumentNullException(nameof(apiConnectionDetails));
            _name = name;

            _configuredRefreshInterval = TimeSpan.FromMinutes(bearerTokenRefreshMinutes);
            _refreshInterval = _configuredRefreshInterval;

            string apiUrl =
                apiConnectionDetails.Url
                ?? throw new InvalidOperationException("URL for API connection '{name}' was not assigned.");

            _dataManagementApiSegment = new Lazy<string>(
                () =>
                    ConnectionDetails.SchoolYear == null
                        ? EdFiApiConstants.DataManagementApiSegment
                        : $"{EdFiApiConstants.DataManagementApiSegment}/{ConnectionDetails.SchoolYear}"
            );

            _changeQueriesApiSegment = new Lazy<string>(
                () =>
                    ConnectionDetails.SchoolYear == null
                        ? EdFiApiConstants.ChangeQueriesApiSegment
                        : $"{EdFiApiConstants.ChangeQueriesApiSegment}/{ConnectionDetails.SchoolYear}"
            );

            httpClientHandler ??= new HttpClientHandler();

            if (ignoreSslErrors)
            {
#pragma warning disable S4830 // Server certificates should be verified during SSL/TLS connections
                httpClientHandler.ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
#pragma warning restore S4830 // Server certificates should be verified during SSL/TLS connections
            }

            // The handler applies the bearer token to every request and recovers from an expired token. It only calls
            // back into this instance once a request is sent, which cannot happen before construction completes.
            _httpClient = new HttpClient(new BearerTokenHandler(httpClientHandler, this, _name))
            {
                BaseAddress = new Uri(apiUrl.EnsureSuffixApplied("/"))
            };

            AddProductInfoToRequestHeader(_httpClient);

            // Create a separate HttpClient for token refreshes to avoid possible "Snapshot-Identifier" header presence
            _tokenRefreshHttpClient = new HttpClient(httpClientHandler)
            {
                BaseAddress = new Uri(apiConnectionDetails.AuthUrl?.EnsureSuffixApplied("/") ?? apiUrl.EnsureSuffixApplied("/"))
            };

            AddProductInfoToRequestHeader(_tokenRefreshHttpClient);

            // Get initial bearer token for Ed-Fi ODS API
            try
            {
                AcquireInitialBearerToken();
            }
            catch (EdFiApiAuthenticationException)
            {
                _httpClient.Dispose();
                _tokenRefreshHttpClient.Dispose();
                _tokenRefreshLock.Dispose();

                throw;
            }

            // Refresh the bearer token periodically, rescheduling after every attempt so that a failed refresh is
            // retried on a short delay instead of waiting out a full interval
            _bearerTokenRefreshTimer = new Timer(
                RefreshBearerTokenOnTimer,
                null,
                _refreshInterval,
                Timeout.InfiniteTimeSpan
            );

            static void AddProductInfoToRequestHeader(HttpClient httpClient)
            {
                var assembly = Assembly.GetExecutingAssembly();
                var fileVersion = FileVersionInfo.GetVersionInfo(assembly.Location);
                var version = fileVersion.FileVersion;
                var productInfo = new ProductInfoHeaderValue("Ed-Fi-API-Publisher", version);

                var targetFrameWorkAttributes = assembly.CustomAttributes.Where(attribute =>
                    attribute.AttributeType.Name == nameof(TargetFrameworkAttribute)
                );
                var customAttribute = targetFrameWorkAttributes.FirstOrDefault();
                var customAttributeValue = customAttribute?.NamedArguments.FirstOrDefault();
                if (customAttributeValue != null)
                {
                    var dotnetVersionValues = ((CustomAttributeNamedArgument)customAttributeValue).TypedValue.Value.ToString().Split(' ');
                    if (dotnetVersionValues.Length > 0)
                    {
                        var dotnetInfo = new ProductInfoHeaderValue(
                            dotnetVersionValues[0],
                            dotnetVersionValues[1]
                        );
                        httpClient.DefaultRequestHeaders.UserAgent.Add(dotnetInfo);
                    }
                }
                httpClient.DefaultRequestHeaders.UserAgent.Add(productInfo);
            }
        }

        public HttpClient HttpClient => _httpClient;

        public string Name => _name;

        public string CurrentBearerToken => _bearerToken;

        public bool IsAuthenticationFailed => _authenticationFailed;

        public async Task<bool> TryReacquireBearerTokenAsync(
            string staleBearerToken,
            CancellationToken cancellationToken
        )
        {
            if (_authenticationFailed)
            {
                return false;
            }

            await _tokenRefreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (!string.Equals(_bearerToken, staleBearerToken, StringComparison.Ordinal))
                {
                    // Another request already replaced the token that was rejected. An API that issues a byte for
                    // byte identical token would not be recognized here, which costs an extra token request per
                    // rejected request but is still correct.
                    return true;
                }

                _logger.Information(
                    "Re-acquiring bearer token for {Name} API client after an unauthorized response.",
                    _name.ToLower()
                );

                await AcquireBearerTokenAsync(cancellationToken).ConfigureAwait(false);

                return true;
            }
            catch (Exception ex)
            {
                int consecutiveFailures = Interlocked.Increment(ref _consecutiveTokenFailures);

                // The token has already been rejected, so whatever lifetime the API reported for it says nothing
                // about the time left to recover. Here the failure count is all there is to go on.
                if (
                    !BearerTokenRefreshPolicy.TryGetRetryDelay(
                        consecutiveFailures,
                        remainingTokenLifetime: null,
                        out _
                    )
                )
                {
                    _authenticationFailed = true;

                    _logger.Fatal(
                        ex,
                        "Re-acquisition of the bearer token after an unauthorized response failed for {Name} API client, which has now failed {ConsecutiveFailures} consecutive times. Publishing cannot continue and the remaining requests will fail.",
                        _name.ToLower(),
                        consecutiveFailures
                    );
                }
                else
                {
                    _logger.Error(
                        ex,
                        "Re-acquisition of the bearer token after an unauthorized response failed for {Name} API client (consecutive failure {ConsecutiveFailures} of {MaxConsecutiveFailures}).",
                        _name.ToLower(),
                        consecutiveFailures,
                        BearerTokenRefreshPolicy.MaxConsecutiveFailuresWithUnknownLifetime
                    );
                }

                return false;
            }
            finally
            {
                _tokenRefreshLock.Release();
            }
        }

        private async Task<BearerTokenResponse> GetBearerTokenAsync(
            HttpClient httpClient,
            string key,
            string secret,
            string scope,
            bool isOdsApiAuth,
            CancellationToken cancellationToken
        )
        {
            if (_logger.IsEnabled(LogEventLevel.Debug))
                _logger.Debug(
                    "Getting bearer token for {Name} API client with key {Key}...",
                    _name,
                    key[..3]
                );

            var authRequest = new HttpRequestMessage(HttpMethod.Post, isOdsApiAuth ? "oauth/token" : string.Empty);
            string encodedKeyAndSecret = Base64Encode($"{key}:{secret}");

            string bodyContent =
                "grant_type=client_credentials"
                + (string.IsNullOrEmpty(scope) ? null : $"&scope={HttpUtility.UrlEncode(scope)}");

            authRequest.Content = new StringContent(
                bodyContent,
                Encoding.UTF8,
                "application/x-www-form-urlencoded"
            );

            authRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", encodedKeyAndSecret);

            if (_logger.IsEnabled(LogEventLevel.Debug))
            {
                if (string.IsNullOrEmpty(scope))
                {
                    _logger.Debug(
                        "Sending token request for {Name} API client to '{Method} {Uri}'...",
                        _name.ToLower(),
                        authRequest.Method,
                        authRequest.RequestUri
                    );
                }
                else
                {
                    _logger.Debug(
                        "Sending token request for {Name} API client to '{Method} {Uri}' with scope '{Scope}'...",
                        _name.ToLower(),
                        authRequest.Method,
                        authRequest.RequestUri,
                        scope
                    );
                }
            }

            var authResponseMessage = await httpClient
                .SendAsync(authRequest, cancellationToken)
                .ConfigureAwait(false);
            string authResponseContent = await authResponseMessage
                .Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!authResponseMessage.IsSuccessStatusCode)
            {
                _logger.Error(
                    "Authentication of {Name} API client against '{Uri}' failed. {Method} request returned status {StatusCode}:\r{Content}",
                    _name.ToLower(),
                    authRequest.RequestUri,
                    authRequest.Method,
                    authResponseMessage.StatusCode,
                    authResponseContent
                );
                throw new EdFiApiAuthenticationException(
                    $"Authentication failed for {_name.ToLower()} API client."
                );
            }

            var authResponseObject = JObject.Parse(authResponseContent);

            if (!string.IsNullOrEmpty(scope))
            {
                if (scope != authResponseObject["scope"]?.Value<string>())
                {
                    throw new EdFiApiAuthenticationException(
                        $"Authentication was successful for {_name.ToLower()} API client but the requested scope of '{scope}' was not honored by the host. Remove the 'scope' parameter from the connection information for this API endpoint to proceed with an unscoped access token."
                    );
                }

                if (_logger.IsEnabled(LogEventLevel.Debug))
                {
                    _logger.Debug(
                        "Token request for {Name} API client with scope '{Scope}' was returned by server.",
                        _name.ToLower(),
                        scope
                    );
                }
            }

            string bearerToken = authResponseObject["access_token"].Value<string>();

            return new BearerTokenResponse(bearerToken, GetTokenLifetimeSeconds(authResponseObject));
        }

        private static int? GetTokenLifetimeSeconds(JObject authResponseObject)
        {
            var expiresInToken = authResponseObject["expires_in"];

            if (expiresInToken != null && int.TryParse(expiresInToken.ToString(), out int expiresInSeconds))
            {
                return expiresInSeconds;
            }

            return null;
        }

        private static string Base64Encode(string plainText)
        {
            var plainTextBytes = Encoding.UTF8.GetBytes(plainText);
            return Convert.ToBase64String(plainTextBytes);
        }

        /// <summary>
        /// Obtains the bearer token that publishing cannot start without. A failure here is terminal, since every
        /// subsequent request would be rejected, so the exception is allowed to end the run.
        /// </summary>
        private void AcquireInitialBearerToken()
        {
            _logger.Information("Retrieving initial bearer token for {Name} API client.", _name.ToLower());

            try
            {
                // No synchronization is needed here, because nothing else can be using the client yet.
                AcquireBearerTokenAsync(CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _authenticationFailed = true;

                throw new EdFiApiAuthenticationException(
                    $"Unable to obtain initial bearer token for {_name.ToLower()} API client.",
                    ex
                );
            }

            _logger.Information(
                "Bearer token retrieved successfully for {Name} API client.",
                _name.ToLower()
            );
        }

        private async Task AcquireBearerTokenAsync(CancellationToken cancellationToken)
        {
            var tokenResponse = await GetBearerTokenAsync(
                    _tokenRefreshHttpClient,
                    ConnectionDetails.Key,
                    ConnectionDetails.Secret,
                    ConnectionDetails.Scope,
                    ConnectionDetails.IsOdsAuthService,
                    cancellationToken
                )
                .ConfigureAwait(false);

            _bearerToken = tokenResponse.AccessToken;

            var tokenLifetime = tokenResponse.ExpiresInSeconds is > 0
                ? TimeSpan.FromSeconds(tokenResponse.ExpiresInSeconds.Value)
                : (TimeSpan?)null;

            Interlocked.Exchange(
                ref _tokenExpiresAtUtcTicks,
                tokenLifetime == null ? 0 : DateTime.UtcNow.Add(tokenLifetime.Value).Ticks
            );

            Interlocked.Exchange(ref _consecutiveTokenFailures, 0);

            // Retained for callers that read the header directly. Outgoing requests take the token from the request
            // pipeline instead, which is also what keeps a refresh from racing with requests already in flight.
            HttpClient.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(
                $"Bearer {tokenResponse.AccessToken}"
            );

            ApplyRefreshInterval(tokenLifetime);
        }

        /// <summary>
        /// Applies the refresh interval derived from the lifetime the API reports for the token, so that a failed
        /// refresh can be retried before the token expires.
        /// </summary>
        private void ApplyRefreshInterval(TimeSpan? tokenLifetime)
        {
            var interval = BearerTokenRefreshPolicy.GetRefreshInterval(_configuredRefreshInterval, tokenLifetime);

            if (interval == _refreshInterval)
            {
                return;
            }

            _logger.Information(
                "Bearer token refresh interval for {Name} API client set to {IntervalMinutes:N1} minutes (configured interval: {ConfiguredMinutes:N1} minutes, token lifetime reported by the API: {TokenLifetimeMinutes:N1} minutes).",
                _name.ToLower(),
                interval.TotalMinutes,
                _configuredRefreshInterval.TotalMinutes,
                tokenLifetime?.TotalMinutes
            );

            _refreshInterval = interval;
        }

        /// <summary>
        /// Gets what is left of the current token's lifetime, or <b>null</b> when the API reported no lifetime.
        /// </summary>
        private TimeSpan? GetRemainingTokenLifetime()
        {
            long expiresAtUtcTicks = Interlocked.Read(ref _tokenExpiresAtUtcTicks);

            if (expiresAtUtcTicks == 0)
            {
                return null;
            }

            return new DateTime(expiresAtUtcTicks, DateTimeKind.Utc) - DateTime.UtcNow;
        }

        private void RefreshBearerTokenOnTimer(object state)
        {
            TimeSpan nextRefreshDelay;

            try
            {
                _logger.Information("Refreshing bearer token for {Name} API client.", _name.ToLower());

                _tokenRefreshLock.Wait();

                try
                {
                    AcquireBearerTokenAsync(CancellationToken.None)
                        .ConfigureAwait(false)
                        .GetAwaiter()
                        .GetResult();
                }
                finally
                {
                    _tokenRefreshLock.Release();
                }

                _logger.Information(
                    "Bearer token refreshed successfully for {Name} API client.",
                    _name.ToLower()
                );

                nextRefreshDelay = _refreshInterval;
            }
            catch (Exception ex)
            {
                // A timer callback must never throw, so the outcome is reported through the failure count instead.
                int consecutiveFailures = Interlocked.Increment(ref _consecutiveTokenFailures);

                // The current token stays valid until it expires, so publishing is unaffected by a failed refresh
                // until then. That remaining lifetime is what decides whether there is still time to recover.
                if (
                    !BearerTokenRefreshPolicy.TryGetRetryDelay(
                        consecutiveFailures,
                        GetRemainingTokenLifetime(),
                        out nextRefreshDelay
                    )
                )
                {
                    _authenticationFailed = true;

                    _logger.Fatal(
                        ex,
                        "Refresh of the bearer token for {Name} API client has failed {ConsecutiveFailures} consecutive times and can no longer be retried before the token expires. Publishing cannot continue and the remaining requests will fail.",
                        _name.ToLower(),
                        consecutiveFailures
                    );

                    return;
                }

                _logger.Error(
                    ex,
                    "Refresh of the bearer token failed for {Name} API client (consecutive failure {ConsecutiveFailures}). The current token is still valid, so the refresh is retried in {DelaySeconds:N0} seconds.",
                    _name.ToLower(),
                    consecutiveFailures,
                    nextRefreshDelay.TotalSeconds
                );
            }

            try
            {
                _bearerTokenRefreshTimer?.Change(nextRefreshDelay, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // The client was disposed while the token was being refreshed.
            }
        }

        public ApiConnectionDetails ConnectionDetails { get; }

        public string DataManagementApiSegment => _dataManagementApiSegment.Value;

        public string ChangeQueriesApiSegment => _changeQueriesApiSegment.Value;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _httpClient?.Dispose();
                _bearerTokenRefreshTimer?.Dispose();
                _tokenRefreshHttpClient?.Dispose();
                _tokenRefreshLock?.Dispose();
            }
        }

        private sealed record BearerTokenResponse(string AccessToken, int? ExpiresInSeconds);
    }
}
