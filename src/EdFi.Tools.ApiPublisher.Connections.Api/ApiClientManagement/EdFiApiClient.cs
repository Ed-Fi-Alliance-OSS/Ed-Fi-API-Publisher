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

        // The transport is shared by the request pipeline and by the token manager, and owned here: it is created or
        // taken over by this client and disposed by it, after both of the things that send through it.
        private readonly HttpClientHandler _httpClientHandler;
        private readonly HttpClient _httpClient;
        private readonly BearerTokenManager _bearerTokenManager;

        private readonly Lazy<string> _dataManagementApiSegment;
        private readonly Lazy<string> _changeQueriesApiSegment;

        public EdFiApiClient(
            string name,
            ApiConnectionDetails apiConnectionDetails,
            int bearerTokenRefreshMinutes,
            bool ignoreSslErrors,
            HttpClientHandler httpClientHandler = null,
            TimeProvider timeProvider = null
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
                    ConnectionDetails.SchoolYear is null
                        ? EdFiApiConstants.DataManagementApiSegment
                        : $"{EdFiApiConstants.DataManagementApiSegment}/{ConnectionDetails.SchoolYear}"
            );

            _changeQueriesApiSegment = new Lazy<string>(
                () =>
                    ConnectionDetails.SchoolYear is null
                        ? EdFiApiConstants.ChangeQueriesApiSegment
                        : $"{EdFiApiConstants.ChangeQueriesApiSegment}/{ConnectionDetails.SchoolYear}"
            );

            _httpClientHandler = httpClientHandler ?? new HttpClientHandler();

            if (ignoreSslErrors)
            {
#pragma warning disable S4830 // Server certificates should be verified during SSL/TLS connections
                _httpClientHandler.ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
#pragma warning restore S4830 // Server certificates should be verified during SSL/TLS connections
            }

            try
            {
                // The token manager is created first and obtains the initial token, so a connection that cannot
                // authenticate fails here rather than on the first request.
                _bearerTokenManager = new BearerTokenManager(
                    name,
                    apiConnectionDetails,
                    bearerTokenRefreshMinutes,
                    _httpClientHandler,
                    timeProvider
                );

                // The handler applies the token to every request and recovers from one the API rejects. It reads the
                // token from the manager, which is why nothing here has to be published before it is fully built.
                // Neither client disposes the transport; that is done here, once, after both are gone.
                _httpClient = new HttpClient(
                    new BearerTokenHandler(_httpClientHandler, _bearerTokenManager, name),
                    disposeHandler: false
                )
                {
                    BaseAddress = new Uri(apiUrl.EnsureSuffixApplied("/"))
                };
            }
            catch
            {
                _bearerTokenManager?.Dispose();
                _httpClientHandler.Dispose();

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
                // The manager goes first so that its timer cannot start a token request on a transport that is
                // about to be disposed; the transport goes last, once nothing sends through it any more.
                _bearerTokenManager?.Dispose();
                _httpClient?.Dispose();
                _httpClientHandler?.Dispose();
            }
        }
    }
}
