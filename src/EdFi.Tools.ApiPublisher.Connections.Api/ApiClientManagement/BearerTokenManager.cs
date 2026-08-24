// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Http.Headers;
using System.Text;
using System.Web;
using EdFi.Tools.ApiPublisher.Connections.Api.Configuration;
using EdFi.Tools.ApiPublisher.Core.Extensions;
using Newtonsoft.Json.Linq;
using Serilog;
using Serilog.Events;

namespace EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement
{
    /// <summary>
    /// Owns the bearer token for one API connection: obtaining it, refreshing it before it expires, re-acquiring it
    /// when the API rejects it, and deciding when it can no longer be obtained at all.
    /// </summary>
    /// <remarks>
    /// This is deliberately separate from <see cref="EdFiApiClient" />. It owns the client used to reach the token
    /// endpoint, which is built on the transport handler directly, so a token request cannot be routed back through
    /// the handler that recovers from a rejected token. Keeping the token state, its timer, its lock and its failure
    /// counters together also keeps the synchronization they need in one place.
    /// </remarks>
    public class BearerTokenManager : IBearerTokenProvider, IDisposable
    {
        /// <summary>
        /// How much of a failed token request's response body is logged, so that a verbose auth server cannot fill
        /// the log now that a failed refresh is retried on a short backoff.
        /// </summary>
        private const int MaxLoggedAuthResponseLength = 1000;

        private readonly ApiConnectionDetails _connectionDetails;
        private readonly string _name;
        private readonly string _displayName;
        private readonly ILogger _logger = Log.ForContext(typeof(BearerTokenManager));

        private readonly HttpClient _tokenRequestHttpClient;
        private readonly Timer _refreshTimer;
        private readonly TimeSpan _configuredRefreshInterval;
        private readonly SemaphoreSlim _tokenRefreshLock = new(1, 1);

        private volatile string _bearerToken;
        private volatile bool _authenticationFailed;
        private int _consecutiveTokenFailures;

        // Ticks rather than a TimeSpan so that the timer callback and the request path, which write and read these
        // outside of one another's lock, cannot observe a partially written value.
        private long _refreshIntervalTicks;

        // Ticks of the UTC instant at which the current token expires, or 0 when the API reports no lifetime.
        private long _tokenExpiresAtUtcTicks;

        public BearerTokenManager(
            string name,
            ApiConnectionDetails connectionDetails,
            int bearerTokenRefreshMinutes,
            HttpClientHandler httpClientHandler
        )
        {
            _connectionDetails =
                connectionDetails ?? throw new ArgumentNullException(nameof(connectionDetails));
            _name = name;
            _displayName = name?.ToLower();

            _configuredRefreshInterval = TimeSpan.FromMinutes(bearerTokenRefreshMinutes);
            _refreshIntervalTicks = _configuredRefreshInterval.Ticks;

            string tokenEndpointUrl =
                connectionDetails.AuthUrl
                ?? connectionDetails.Url
                ?? throw new InvalidOperationException(
                    $"Neither an authentication URL nor an API URL was assigned for API connection '{name}'."
                );

            // Built on the transport handler itself, so a token request never passes through the handler that
            // recovers from a rejected token. It is also what keeps the "Snapshot-Identifier" header off these
            // requests.
            _tokenRequestHttpClient = new HttpClient(httpClientHandler)
            {
                BaseAddress = new Uri(tokenEndpointUrl.EnsureSuffixApplied("/"))
            };

            ApiPublisherProductInfo.ApplyTo(_tokenRequestHttpClient);

            try
            {
                AcquireInitialBearerToken();
            }
            catch (EdFiApiAuthenticationException)
            {
                _tokenRequestHttpClient.Dispose();
                _tokenRefreshLock.Dispose();

                throw;
            }

            // Rescheduled after every attempt, so that a failed refresh is retried on a short delay instead of
            // waiting out a full interval
            _refreshTimer = new Timer(RefreshBearerTokenOnTimer, null, RefreshInterval, Timeout.InfiniteTimeSpan);
        }

        public string CurrentBearerToken => _bearerToken;

        public bool IsAuthenticationFailed => _authenticationFailed;

        /// <summary>
        /// The interval currently in effect between token refreshes, which is derived from the lifetime the API
        /// reports for the token and bounded by the configured interval.
        /// </summary>
        public TimeSpan RefreshInterval => TimeSpan.FromTicks(Interlocked.Read(ref _refreshIntervalTicks));

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
                    _displayName
                );

                await AcquireBearerTokenAsync(cancellationToken).ConfigureAwait(false);

                // The periodic refresh is now due from this acquisition rather than from the previous one.
                RescheduleRefresh(RefreshInterval);

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
                        "Re-acquisition of the bearer token after an unauthorized response failed for {Name} API client ({FailureCount} on the request path). Publishing cannot continue and the remaining requests will fail.",
                        _displayName,
                        DescribeFailureCount(consecutiveFailures)
                    );
                }
                else
                {
                    _logger.Warning(
                        ex,
                        "Re-acquisition of the bearer token after an unauthorized response failed for {Name} API client ({FailureCount} on the request path, of {MaxConsecutiveFailures} tolerated).",
                        _displayName,
                        DescribeFailureCount(consecutiveFailures),
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

        /// <summary>
        /// Performs one refresh attempt and reports how long to wait before the next one, or <b>null</b> when the
        /// timer should not be rearmed at all. Separated from the timer callback so that the decision and the
        /// rescheduling it produces can be exercised without waiting for a timer to fire.
        /// </summary>
        public TimeSpan? TryRefreshBearerToken()
        {
            if (_authenticationFailed)
            {
                // Nothing is left to recover: either this path or the request path has already given up.
                return null;
            }

            try
            {
                _logger.Information("Refreshing bearer token for {Name} API client.", _displayName);

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

                _logger.Information("Bearer token refreshed successfully for {Name} API client.", _displayName);

                return RefreshInterval;
            }
            catch (ObjectDisposedException)
            {
                // The manager was disposed while the token was being refreshed, which is an ordinary shutdown and
                // not a refresh failure.
                return null;
            }
            catch (Exception ex)
            {
                // A timer callback must never throw, so the outcome is reported through the return value instead.
                int consecutiveFailures = Interlocked.Increment(ref _consecutiveTokenFailures);
                var remainingTokenLifetime = GetRemainingTokenLifetime();

                // The current token stays valid until it expires, so publishing is unaffected by a failed refresh
                // until then. That remaining lifetime is what decides whether there is still time to recover.
                if (
                    !BearerTokenRefreshPolicy.TryGetRetryDelay(
                        consecutiveFailures,
                        remainingTokenLifetime,
                        out var retryDelay
                    )
                )
                {
                    _authenticationFailed = true;

                    _logger.Fatal(
                        ex,
                        "Refresh of the bearer token for {Name} API client failed and can no longer be retried before the token expires ({FailureCount}, remaining token lifetime: {RemainingLifetime}). Publishing cannot continue and the remaining requests will fail.",
                        _displayName,
                        DescribeFailureCount(consecutiveFailures),
                        DescribeRemainingLifetime(remainingTokenLifetime)
                    );

                    return null;
                }

                _logger.Warning(
                    ex,
                    "Refresh of the bearer token failed for {Name} API client ({FailureCount}). The current token is still valid (remaining lifetime: {RemainingLifetime}), so the refresh is retried in {DelaySeconds:N0} seconds.",
                    _displayName,
                    DescribeFailureCount(consecutiveFailures),
                    DescribeRemainingLifetime(remainingTokenLifetime),
                    retryDelay.TotalSeconds
                );

                return retryDelay;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _refreshTimer?.Dispose();
                _tokenRequestHttpClient?.Dispose();
                _tokenRefreshLock?.Dispose();
            }
        }

        /// <summary>
        /// Obtains the bearer token that publishing cannot start without. A failure here is terminal, since every
        /// subsequent request would be rejected, so the exception is allowed to end the run.
        /// </summary>
        private void AcquireInitialBearerToken()
        {
            _logger.Information("Retrieving initial bearer token for {Name} API client.", _displayName);

            try
            {
                // No synchronization is needed here, because nothing else can be using the manager yet.
                AcquireBearerTokenAsync(CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _authenticationFailed = true;

                throw new EdFiApiAuthenticationException(
                    $"Unable to obtain initial bearer token for {_displayName} API client.",
                    ex
                );
            }

            _logger.Information("Bearer token retrieved successfully for {Name} API client.", _displayName);
        }

        private async Task AcquireBearerTokenAsync(CancellationToken cancellationToken)
        {
            var tokenResponse = await GetBearerTokenAsync(cancellationToken).ConfigureAwait(false);

            _bearerToken = tokenResponse.AccessToken;

            var tokenLifetime = tokenResponse.ExpiresInSeconds is > 0
                ? TimeSpan.FromSeconds(tokenResponse.ExpiresInSeconds.Value)
                : (TimeSpan?)null;

            Interlocked.Exchange(
                ref _tokenExpiresAtUtcTicks,
                tokenLifetime == null ? 0 : DateTime.UtcNow.Add(tokenLifetime.Value).Ticks
            );

            Interlocked.Exchange(ref _consecutiveTokenFailures, 0);

            ApplyRefreshInterval(tokenLifetime);
        }

        private async Task<BearerTokenResponse> GetBearerTokenAsync(CancellationToken cancellationToken)
        {
            string key = _connectionDetails.Key;
            string scope = _connectionDetails.Scope;

            if (_logger.IsEnabled(LogEventLevel.Debug))
                _logger.Debug(
                    "Getting bearer token for {Name} API client with key {Key}...",
                    _name,
                    key[..3]
                );

            var authRequest = new HttpRequestMessage(
                HttpMethod.Post,
                _connectionDetails.IsOdsAuthService ? "oauth/token" : string.Empty
            );

            string encodedKeyAndSecret = Base64Encode($"{key}:{_connectionDetails.Secret}");

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
                        _displayName,
                        authRequest.Method,
                        authRequest.RequestUri
                    );
                }
                else
                {
                    _logger.Debug(
                        "Sending token request for {Name} API client to '{Method} {Uri}' with scope '{Scope}'...",
                        _displayName,
                        authRequest.Method,
                        authRequest.RequestUri,
                        scope
                    );
                }
            }

            var authResponseMessage = await _tokenRequestHttpClient
                .SendAsync(authRequest, cancellationToken)
                .ConfigureAwait(false);
            string authResponseContent = await authResponseMessage
                .Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!authResponseMessage.IsSuccessStatusCode)
            {
                _logger.Error(
                    "Authentication of {Name} API client against '{Uri}' failed. {Method} request returned status {StatusCode}:\r{Content}",
                    _displayName,
                    authRequest.RequestUri,
                    authRequest.Method,
                    authResponseMessage.StatusCode,
                    Truncate(authResponseContent)
                );

                // The status belongs in the message as well as in the log entry above, because this is the message
                // that travels up to the operator when the run ends.
                throw new EdFiApiAuthenticationException(
                    $"Authentication failed for {_displayName} API client: the token request to '{authRequest.RequestUri}' returned status {(int)authResponseMessage.StatusCode} {authResponseMessage.StatusCode}."
                );
            }

            var authResponseObject = JObject.Parse(authResponseContent);

            if (!string.IsNullOrEmpty(scope))
            {
                if (scope != authResponseObject["scope"]?.Value<string>())
                {
                    throw new EdFiApiAuthenticationException(
                        $"Authentication was successful for {_displayName} API client but the requested scope of '{scope}' was not honored by the host. Remove the 'scope' parameter from the connection information for this API endpoint to proceed with an unscoped access token."
                    );
                }

                if (_logger.IsEnabled(LogEventLevel.Debug))
                {
                    _logger.Debug(
                        "Token request for {Name} API client with scope '{Scope}' was returned by server.",
                        _displayName,
                        scope
                    );
                }
            }

            string bearerToken = authResponseObject["access_token"].Value<string>();

            return new BearerTokenResponse(bearerToken, GetTokenLifetimeSeconds(authResponseObject));
        }

        /// <summary>
        /// Applies the refresh interval derived from the lifetime the API reports for the token, so that a failed
        /// refresh can be retried before the token expires.
        /// </summary>
        private void ApplyRefreshInterval(TimeSpan? tokenLifetime)
        {
            var interval = BearerTokenRefreshPolicy.GetRefreshInterval(_configuredRefreshInterval, tokenLifetime);

            if (interval == RefreshInterval)
            {
                return;
            }

            _logger.Information(
                "Bearer token refresh interval for {Name} API client set to {IntervalMinutes:N1} minutes (configured interval: {ConfiguredMinutes:N1} minutes, token lifetime reported by the API: {TokenLifetimeMinutes:N1} minutes).",
                _displayName,
                interval.TotalMinutes,
                _configuredRefreshInterval.TotalMinutes,
                tokenLifetime?.TotalMinutes
            );

            Interlocked.Exchange(ref _refreshIntervalTicks, interval.Ticks);
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
            var nextRefreshDelay = TryRefreshBearerToken();

            if (nextRefreshDelay != null)
            {
                RescheduleRefresh(nextRefreshDelay.Value);
            }
        }

        private void RescheduleRefresh(TimeSpan delay)
        {
            try
            {
                _refreshTimer?.Change(delay, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // The manager was disposed while the token was being refreshed.
            }
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

        private static string Truncate(string content) =>
            content != null && content.Length > MaxLoggedAuthResponseLength
                ? content[..MaxLoggedAuthResponseLength] + "... (truncated)"
                : content;

        private static string DescribeFailureCount(int consecutiveFailures) =>
            consecutiveFailures == 1
                ? "1 consecutive failure"
                : $"{consecutiveFailures} consecutive failures";

        private static string DescribeRemainingLifetime(TimeSpan? remainingTokenLifetime) =>
            remainingTokenLifetime == null
                ? "not reported by the API"
                : $"{remainingTokenLifetime.Value.TotalSeconds:N0}s";

        private sealed record BearerTokenResponse(string AccessToken, int? ExpiresInSeconds);
    }
}
