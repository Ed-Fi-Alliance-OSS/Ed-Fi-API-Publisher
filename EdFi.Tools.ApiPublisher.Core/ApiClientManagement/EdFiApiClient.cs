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
    public class EdFiApiClient
    {
        private readonly ILog _logger = LogManager.GetLogger(typeof(EdFiApiClient));
        
        private readonly HttpClient _httpClient;
        private readonly Timer _bearerTokenRefreshTimer;
        private readonly HttpClient _tokenRefreshHttpClient;

        public EdFiApiClient(
            ApiConnectionDetails apiConnectionDetails,
            int bearerTokenRefreshMinutes,
            bool ignoreSslErrors)
        {
            ConnectionDetails = apiConnectionDetails;

            var httpClientHandler = new HttpClientHandler();

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
                _tokenRefreshHttpClient,
                TimeSpan.FromMinutes(bearerTokenRefreshMinutes),
                TimeSpan.FromMinutes(bearerTokenRefreshMinutes));
        }

        public HttpClient HttpClient => _httpClient;

        private async Task<string> GetBearerTokenAsync(HttpClient httpClient, string key, string secret, string scope)
        {
            if (_logger.IsDebugEnabled)
                _logger.Debug($"Getting bearer token for key {key.Substring(0, 3)}...");
            
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
                    _logger.Debug($"Sending token request to '{authRequest.Method} {authRequest.RequestUri}'...");
                }
                else
                {
                    _logger.Debug($"Sending token request to '{authRequest.Method} {authRequest.RequestUri}' with scope '{scope}'...");
                }
            }

            var authResponseMessage = await httpClient.SendAsync(authRequest).ConfigureAwait(false);
            string authResponseContent = await authResponseMessage.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!authResponseMessage.IsSuccessStatusCode)
            {
                _logger.Error($"Authentication against '{authRequest.RequestUri}' failed. {authRequest.Method} request returned status {authResponseMessage.StatusCode}:{Environment.NewLine}{authResponseContent}");
                throw new Exception("Authentication failed.");
            }

            var authResponseObject = JObject.Parse(authResponseContent);
            
            if (!string.IsNullOrEmpty(scope))
            {
                if (scope != authResponseObject["scope"].Value<string>())
                {
                    throw new Exception($"Authentication was successful but the requested scope of '{scope}' was not honored by the host. Remove the 'scope' parameter from the connection information for this API endpoint to proceed with an unscoped access token.");
                }

                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"Token request with scope '{scope}' was returned by server.");
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
            bool isInitializing = ((bool?) state).GetValueOrDefault();
            
            if (isInitializing)
            {
                _logger.Info("Retrieving initial bearer token.");
            }
            else
            {
                _logger.Info("Refreshing bearer token.");
            }

            try
            {
                var bearerToken = GetBearerTokenAsync(_tokenRefreshHttpClient, ConnectionDetails.Key, ConnectionDetails.Secret, ConnectionDetails.Scope)
                    .ConfigureAwait(false).GetAwaiter().GetResult();

                HttpClient.DefaultRequestHeaders.Authorization =
                    AuthenticationHeaderValue.Parse($"Bearer {bearerToken}");

                if (isInitializing)
                {
                    _logger.Info("Bearer token retrieved successfully.");
                }
                else
                {
                    _logger.Info("Bearer token refreshed successfully.");
                }
            }
            catch (Exception ex)
            {
                if (isInitializing)
                {
                    throw new Exception("Unable to obtain initial bearer token.", ex);
                }
                
                _logger.Error($"Refresh of bearer token failed. Token may expire soon resulting in 401 responses.{Environment.NewLine}{ex}");
            }
        }

        public ApiConnectionDetails ConnectionDetails { get; }
    }
}