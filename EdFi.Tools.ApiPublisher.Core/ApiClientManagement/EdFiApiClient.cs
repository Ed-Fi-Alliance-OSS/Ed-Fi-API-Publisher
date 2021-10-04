using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Extensions;
using log4net;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Core.ApiClientManagement
{
    public class EdFiApiClient : IDisposable
    {
        private readonly string _name;
        private readonly ILog _logger = LogManager.GetLogger(typeof(EdFiApiClient));
        
        private readonly HttpClient _httpClient;
        private readonly Timer _bearerTokenRefreshTimer;
        private readonly HttpClient _tokenRefreshHttpClient;

        public EdFiApiClient(
            string name,
            ApiConnectionDetails apiConnectionDetails,
            int bearerTokenRefreshMinutes,
            bool ignoreSslErrors,
            HttpClientHandler httpClientHandler = null)
        {
            _name = name;
            ConnectionDetails = apiConnectionDetails;

            httpClientHandler ??= new HttpClientHandler();

            if (ignoreSslErrors)
            {
                httpClientHandler.ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            }

            _httpClient = new HttpClient(httpClientHandler)
            {
                BaseAddress = new Uri(apiConnectionDetails.Url.EnsureSuffixApplied("/"))
            };

            // Create a separate HttpClient for token refreshes to avoid possible "Snapshot-Identifier" header presence
            _tokenRefreshHttpClient = new HttpClient(httpClientHandler)
            {
                BaseAddress = new Uri(apiConnectionDetails.Url.EnsureSuffixApplied("/"))
            };

            // Get initial bearer token for Ed-Fi ODS API
            RefreshBearerToken(true);
            
            // Refresh the bearer tokens periodically
            _bearerTokenRefreshTimer = new Timer(RefreshBearerToken,
                false,
                TimeSpan.FromMinutes(bearerTokenRefreshMinutes),
                TimeSpan.FromMinutes(bearerTokenRefreshMinutes));
        }

        public HttpClient HttpClient => _httpClient;

        private async Task<string> GetBearerTokenAsync(HttpClient httpClient, string key, string secret, string scope)
        {
            if (_logger.IsDebugEnabled)
                _logger.Debug($"Getting bearer token for {_name} API client with key {key.Substring(0, 3)}...");
            
            var authRequest = new HttpRequestMessage(HttpMethod.Post, "oauth/token");
            string encodedKeyAndSecret = Base64Encode($"{key}:{secret}");

            string bodyContent = "grant_type=client_credentials"
                + (string.IsNullOrEmpty(scope)
                    ? null
                    : $"&scope={HttpUtility.UrlEncode(scope)}");
            
            authRequest.Content = new StringContent(bodyContent,
                Encoding.UTF8, "application/x-www-form-urlencoded");

            authRequest.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Basic",
                    encodedKeyAndSecret);

            if (_logger.IsDebugEnabled)
            {
                if (string.IsNullOrEmpty(scope))
                {
                    _logger.Debug($"Sending token request for {_name.ToLower()} API client to '{authRequest.Method} {authRequest.RequestUri}'...");
                }
                else
                {
                    _logger.Debug($"Sending token request for {_name.ToLower()} API client to '{authRequest.Method} {authRequest.RequestUri}' with scope '{scope}'...");
                }
            }

            var authResponseMessage = await httpClient.SendAsync(authRequest).ConfigureAwait(false);
            string authResponseContent = await authResponseMessage.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!authResponseMessage.IsSuccessStatusCode)
            {
                _logger.Error($"Authentication of {_name.ToLower()} API client against '{authRequest.RequestUri}' failed. {authRequest.Method} request returned status {authResponseMessage.StatusCode}:{Environment.NewLine}{authResponseContent}");
                throw new Exception($"Authentication failed for {_name.ToLower()} API client.");
            }

            var authResponseObject = JObject.Parse(authResponseContent);
            
            if (!string.IsNullOrEmpty(scope))
            {
                if (scope != authResponseObject["scope"].Value<string>())
                {
                    throw new Exception($"Authentication was successful for {_name.ToLower()} API client but the requested scope of '{scope}' was not honored by the host. Remove the 'scope' parameter from the connection information for this API endpoint to proceed with an unscoped access token.");
                }

                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"Token request for {_name.ToLower()} API client with scope '{scope}' was returned by server.");
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
                bool isInitializing = ((bool?) state).GetValueOrDefault();
            
                if (isInitializing)
                {
                    _logger.Info($"Retrieving initial bearer token for {_name.ToLower()} API client.");
                }
                else
                {
                    _logger.Info($"Refreshing bearer token for {_name.ToLower()} API client.");
                }

                try
                {
                    var bearerToken = GetBearerTokenAsync(_tokenRefreshHttpClient, ConnectionDetails.Key, ConnectionDetails.Secret, ConnectionDetails.Scope)
                        .ConfigureAwait(false).GetAwaiter().GetResult();

                    HttpClient.DefaultRequestHeaders.Authorization =
                        AuthenticationHeaderValue.Parse($"Bearer {bearerToken}");

                    if (isInitializing)
                    {
                        _logger.Info($"Bearer token retrieved successfully for {_name.ToLower()} API client.");
                    }
                    else
                    {
                        _logger.Info($"Bearer token refreshed successfully for {_name.ToLower()} API client.");
                    }
                }
                catch (Exception ex)
                {
                    if (isInitializing)
                    {
                        throw new Exception($"Unable to obtain initial bearer token for {_name.ToLower()} API client.", ex);
                    }
                
                    _logger.Error($"Refresh of bearer token failed for {_name.ToLower()} API client. Token may expire soon resulting in 401 responses.{Environment.NewLine}{ex}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"An unhandled exception occurred during bearer token refresh. Token expiration may occur. {ex}");
            }
        }

        public ApiConnectionDetails ConnectionDetails { get; }

        public void Dispose()
        {
            _httpClient?.Dispose();
            _bearerTokenRefreshTimer?.Dispose();
            _tokenRefreshHttpClient?.Dispose();
        }
    }
}