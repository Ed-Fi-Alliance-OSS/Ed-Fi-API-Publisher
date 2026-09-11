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
            TimeProvider timeProvider = null,
            ApiThrottlingPolicy throttlingPolicy = null
        )
        {
            throttlingPolicy ??= ApiThrottlingPolicy.None;

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

                var pipeline = BuildRequestPipeline(
                    _httpClientHandler,
                    _bearerTokenManager,
                    throttlingPolicy,
                    name,
                    timeProvider
                );

                _httpClient = new HttpClient(pipeline, disposeHandler: false)
                {
                    BaseAddress = new Uri(apiUrl.EnsureSuffixApplied("/")),

                    // Stated rather than left to the HttpClient default, because the handlers above spend their
                    // waits inside it and have to know what they are working against. The default value is the
                    // same 100 seconds HttpClient applies on its own, so nothing about an ordinary request or the
                    // body-read deadlines derived from this changes.
                    Timeout = throttlingPolicy.RequestBudget
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

        /// <summary>
        /// Builds the request pipeline, outermost first: waiting out a rejected read, then the bearer token, then
        /// the cap on concurrent requests, then the transport. Only the middle one is always present.
        /// </summary>
        /// <remarks>
        /// The order is load bearing. Waiting out a 429 goes outermost so the wait is not spent holding a slot
        /// other reads could be using, and so each replay is stamped with a token that is current. The cap goes
        /// innermost so that it bounds what the transport actually has open, and so a request replayed after an
        /// unauthorized response takes a slot of its own like any other request the API has to serve. What that
        /// costs is that a request queued for a slot is already carrying the token it was stamped with on the way
        /// down, so a long enough queue can send one that has since been rotated; that draws a 401 and is
        /// recovered by the handler above it, at the price of one round trip.
        /// </remarks>
        private static HttpMessageHandler BuildRequestPipeline(
            HttpClientHandler transport,
            IBearerTokenProvider bearerTokenProvider,
            ApiThrottlingPolicy throttlingPolicy,
            string name,
            TimeProvider timeProvider
        )
        {
            HttpMessageHandler pipeline =
                throttlingPolicy.MaxConcurrentRequests > 0
                    ? new ConcurrentRequestLimitingHandler(
                        transport,
                        throttlingPolicy.MaxConcurrentRequests,
                        name
                    )
                    : transport;

            // The handler applies the token to every request and recovers from one the API rejects. It reads the
            // token from the provider, which is why nothing here has to be published before it is fully built.
            // Neither client disposes the transport; that is done by the client, once, after both are gone.
            pipeline = new BearerTokenHandler(pipeline, bearerTokenProvider, name);

            if (throttlingPolicy.TooManyRequestsRetryAttempts > 0)
            {
                pipeline = new RetryAfterHandler(pipeline, throttlingPolicy, name, timeProvider);
            }

            return pipeline;
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
