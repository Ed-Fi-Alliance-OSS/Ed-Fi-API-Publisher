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
    public class EdFiApiClient : IDisposable
    {
        private readonly string _name;
        private readonly ILogger _logger = Log.ForContext(typeof(EdFiApiClient));

        private readonly HttpClient _httpClient;
        private readonly Timer _bearerTokenRefreshTimer;
        private readonly HttpClient _tokenRefreshHttpClient;

        private readonly Lazy<string> _dataManagementApiSegment;
        private readonly Lazy<string> _changeQueriesApiSegment;

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

            _httpClient = new HttpClient(httpClientHandler)
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
            RefreshBearerToken(true);

            // Refresh the bearer tokens periodically
            _bearerTokenRefreshTimer = new Timer(
                RefreshBearerToken,
                false,
                TimeSpan.FromMinutes(bearerTokenRefreshMinutes),
                TimeSpan.FromMinutes(bearerTokenRefreshMinutes)
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

        private async Task<string> GetBearerTokenAsync(
            HttpClient httpClient,
            string key,
            string secret,
            string scope,
            bool isOdsApiAuth
        )
        {
            if (_logger.IsEnabled(LogEventLevel.Debug))
                _logger.Debug(
                    "Getting bearer token for {Name} API client with key {Key}...",
                    _name,
                    key[..3]
                );

            var authRequest = new HttpRequestMessage(HttpMethod.Post, isOdsApiAuth ? "oauth/token" : "");
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

            var authResponseMessage = await httpClient.SendAsync(authRequest).ConfigureAwait(false);
            string authResponseContent = await authResponseMessage
                .Content.ReadAsStringAsync()
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
                throw new InvalidOperationException(
                    $"Authentication failed for {_name.ToLower()} API client."
                );
            }

            var authResponseObject = JObject.Parse(authResponseContent);

            if (!string.IsNullOrEmpty(scope))
            {
                if (scope != authResponseObject["scope"]?.Value<string>())
                {
                    throw new InvalidOperationException(
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

            return bearerToken;
        }

        private static string Base64Encode(string plainText)
        {
            var plainTextBytes = Encoding.UTF8.GetBytes(plainText);
            return Convert.ToBase64String(plainTextBytes);
        }

        private void RefreshBearerToken(object state)
        {
            try
            {
                bool isInitializing = ((bool?)state).GetValueOrDefault();

                if (isInitializing)
                {
                    _logger.Information(
                        "Retrieving initial bearer token for {Name} API client.",
                        _name.ToLower()
                    );
                }
                else
                {
                    _logger.Information("Refreshing bearer token for {Name} API client.", _name.ToLower());
                }

                try
                {
                    var bearerToken = GetBearerTokenAsync(
                            _tokenRefreshHttpClient,
                            ConnectionDetails.Key,
                            ConnectionDetails.Secret,
                            ConnectionDetails.Scope,
                            ConnectionDetails.IsOdsAuthService
                        )
                        .ConfigureAwait(false)
                        .GetAwaiter()
                        .GetResult();

                    HttpClient.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(
                        $"Bearer {bearerToken}"
                    );

                    if (isInitializing)
                    {
                        _logger.Information(
                            "Bearer token retrieved successfully for {Name} API client.",
                            _name.ToLower()
                        );
                    }
                    else
                    {
                        _logger.Information(
                            "Bearer token refreshed successfully for {Name} API client.",
                            _name.ToLower()
                        );
                    }
                }
                catch (Exception ex)
                {
                    if (isInitializing)
                    {
                        throw new InvalidOperationException(
                            $"Unable to obtain initial bearer token for {_name.ToLower()} API client.",
                            ex
                        );
                    }

                    _logger.Error(
                        ex,
                        "Refresh of bearer token failed for {Name} API client. Token may expire soon resulting in 401 responses.",
                        _name.ToLower()
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.Error(
                    ex,
                    "An unhandled exception occurred during bearer token refresh. Token expiration may occur."
                );
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
            }
        }
    }
}
