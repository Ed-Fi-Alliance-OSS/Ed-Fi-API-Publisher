// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.Configuration;
using EdFi.Tools.ApiPublisher.Core.Extensions;
using EdFi.Tools.ApiPublisher.Core.Processing;

namespace EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement
{
    public class EdFiApiClient : IDisposable
    {
        private readonly string _name;

        private readonly HttpClient _httpClient;
        private readonly BearerTokenManager _bearerTokenManager;

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

            // The token manager is created first and obtains the initial token, so a connection that cannot
            // authenticate fails here rather than on the first request.
            _bearerTokenManager = new BearerTokenManager(
                name,
                apiConnectionDetails,
                bearerTokenRefreshMinutes,
                httpClientHandler
            );

            try
            {
                // The handler applies the token to every request and recovers from one the API rejects. It reads the
                // token from the manager, which is why nothing here has to be published before it is fully built.
                _httpClient = new HttpClient(
                    new BearerTokenHandler(httpClientHandler, _bearerTokenManager, name)
                )
                {
                    BaseAddress = new Uri(apiUrl.EnsureSuffixApplied("/"))
                };
            }
            catch
            {
                _bearerTokenManager.Dispose();

                throw;
            }

            ApiPublisherProductInfo.ApplyTo(_httpClient);
        }

        public HttpClient HttpClient => _httpClient;

        public string Name => _name;

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
                _bearerTokenManager?.Dispose();
            }
        }
    }
}
