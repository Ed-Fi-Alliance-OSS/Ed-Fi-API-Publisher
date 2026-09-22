// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.Configuration;
using EdFi.Tools.ApiPublisher.Core.Extensions;
using EdFi.Tools.ApiPublisher.Core.Processing;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using System.Net;

namespace EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement
{
    public class EdFiApiClient : IDisposable
    {
        // One extra attempt, far enough apart to outlast a container that is still coming up, close enough
        // that a run against a genuinely dead root is not held open.
        private const int DiscoveryReadRetries = 1;

        private static readonly TimeSpan DiscoveryReadRetryDelay = TimeSpan.FromSeconds(2);

        private readonly string _name;

        // The transport is shared by the request pipeline and by the token manager, and owned here: it is created or
        // taken over by this client and disposed by it, after both of the things that send through it.
        private readonly HttpClientHandler _httpClientHandler;
        private readonly HttpClient _httpClient;
        private readonly BearerTokenManager _bearerTokenManager;

        private readonly ILogger _logger = Log.ForContext(typeof(EdFiApiClient));

        // Read once and shared by the token endpoint and both path segments, because one document states
        // where all three are served. Kept so that anything else needing the API's own description of itself
        // reads it rather than asking the API a second time.
        private readonly DiscoveryDocument _discoveryDocument;

        private readonly string _dataManagementApiSegment;
        private readonly string _changeQueriesApiSegment;

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
                var baseAddress = new Uri(apiUrl.EnsureSuffixApplied("/"));

                // Read before the token is obtained, because the endpoint the token is requested from is taken
                // from this document. The read is anonymous and is sent straight down the transport, so it needs
                // neither a token nor the pipeline that carries one.
                _discoveryDocument = ReadDiscoveryDocument(baseAddress, throttlingPolicy.RequestBudget);

                var tokenEndpointResolver = new EdFiApiTokenEndpointResolver(baseAddress, _name, _logger);

                var tokenEndpoint = tokenEndpointResolver.Resolve(
                    ConnectionDetails.AuthUrl,
                    _discoveryDocument
                );

                // The token is obtained next, so a connection that cannot authenticate fails here rather than on
                // the first request.
                _bearerTokenManager = new BearerTokenManager(
                    name,
                    apiConnectionDetails,
                    bearerTokenRefreshMinutes,
                    _httpClientHandler,
                    tokenEndpoint,
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
                    BaseAddress = baseAddress,

                    // Stated rather than left to the HttpClient default, because the handlers above spend their
                    // waits inside it and have to know what they are working against. The default value is the
                    // same 100 seconds HttpClient applies on its own, so nothing about an ordinary request or the
                    // body-read deadlines derived from this changes.
                    Timeout = throttlingPolicy.RequestBudget
                };

                ApiPublisherProductInfo.ApplyTo(_httpClient);

                // Resolved here rather than on first use, for the same reason the token is obtained here: a
                // connection whose requests cannot be addressed should say so while it is being set up, not
                // from inside a processing block once the source has already been streamed. It also keeps the
                // blocking read off the publishing threads.
                _dataManagementApiSegment = ResolveApiSegment(
                    EdFiApiUrlSegmentResolver.DataManagement,
                    ConnectionDetails.DataManagementUrlSegment
                );

                _changeQueriesApiSegment = ResolveApiSegment(
                    EdFiApiUrlSegmentResolver.ChangeQueries,
                    ConnectionDetails.ChangeQueriesUrlSegment
                );
            }
            catch
            {
                _bearerTokenManager?.Dispose();
                _httpClient?.Dispose();
                _httpClientHandler.Dispose();

                throw;
            }
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
            if (throttlingPolicy.MaxConcurrentRequests < 0)
            {
                // Rejected rather than read as "no cap", so that a caller that meant to cap the API and got the
                // sign wrong is told, instead of being left with an uncapped client that looks configured.
                throw new ArgumentOutOfRangeException(
                    nameof(throttlingPolicy),
                    throttlingPolicy.MaxConcurrentRequests,
                    "A negative cap on concurrent requests is not a way of asking for no cap. Use 0 to leave the API uncapped."
                );
            }

            HttpMessageHandler pipeline =
                throttlingPolicy.MaxConcurrentRequests > 0
                    ? new ConcurrentRequestLimitingHandler(transport, throttlingPolicy, name)
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

        /// <summary>
        /// Reads the API's Discovery document, the anonymous document at the root of the connection's URL in
        /// which the API states where it serves each part of its surface. An API that cannot be asked, or that
        /// answers with something unreadable, yields an empty document and so states nothing.
        /// </summary>
        /// <remarks>
        /// Blocks, because the segments it feeds are read through synchronous properties by every call site
        /// that builds a request. It happens once per client, while that client is constructed and before the
        /// token is requested.
        /// <para>
        /// The address and the request budget are passed in rather than read from the client's own
        /// <see cref="HttpClient" />, because this runs before that client exists: the token endpoint is stated
        /// in the document this reads, so the document has to be in hand before a token can be asked for.
        /// </para>
        /// <para>
        /// The request is anonymous. The version check it replaces carried this connection's bearer token,
        /// but the token endpoint is itself named in the document being read, so asking for one first would
        /// be circular. Every Ed-Fi API serves its Discovery document anonymously; a gateway configured to
        /// demand a token on the connection's root has to exempt it.
        /// </para>
        /// </remarks>
        private DiscoveryDocument ReadDiscoveryDocument(Uri baseAddress, TimeSpan requestBudget)
        {
            // A status a later run could get past is worth one more try now, rather than ending a run over a
            // gateway that is a moment away from being ready. Bounded deliberately: this blocks client
            // construction, and a root that keeps answering 503 is not something waiting here will fix.
            for (int attempt = 1; attempt <= DiscoveryReadRetries + 1; attempt++)
            {
                var document = AttemptDiscoveryDocumentRead(baseAddress, requestBudget);

                if (document.Outcome != DiscoveryOutcome.Unreachable || attempt > DiscoveryReadRetries)
                {
                    return document;
                }

                _logger.Information(
                    "Reading the Discovery document for the {ConnectionName:l} API did not succeed; trying again in {Delay} (attempt {Attempt} of {Total}).",
                    _name,
                    DiscoveryReadRetryDelay,
                    attempt + 1,
                    DiscoveryReadRetries + 1
                );

                Thread.Sleep(DiscoveryReadRetryDelay);
            }

            return DiscoveryDocument.Unread;
        }

        private DiscoveryDocument AttemptDiscoveryDocumentRead(Uri baseAddress, TimeSpan requestBudget)
        {
            using var discoveryRequestHttpClient = new HttpClient(_httpClientHandler, disposeHandler: false)
            {
                BaseAddress = baseAddress,
                Timeout = requestBudget
            };

            ApiPublisherProductInfo.ApplyTo(discoveryRequestHttpClient);

            try
            {
                using var response = discoveryRequestHttpClient.GetAsync("").GetAwaiter().GetResult();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.Warning(
                        "The {ConnectionName:l} API at '{BaseAddress}' answered {StatusCode} for its Discovery document, so the paths it serves cannot be read from it.",
                        _name,
                        baseAddress,
                        (int)response.StatusCode
                    );

                    // An answer is not the same as a final answer. A gateway in front of an API that is
                    // restarting says 503, a saturated one says 429, and a later run gets the document, so
                    // those keep the outcome that says the run may be repeated. Anything else at that
                    // address is not serving a Discovery document and will not tomorrow either.
                    return MayAnswerDifferentlyLater(response.StatusCode)
                        ? DiscoveryDocument.Unread
                        : DiscoveryDocument.Unusable;
                }

                string content = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                try
                {
                    return DiscoveryDocument.Read(JObject.Parse(content));
                }
                catch (JsonException ex)
                {
                    // Also an answer, just not one a document can be read from: a gateway error page or a
                    // sign-in redirect reaches here rather than an Ed-Fi API.
                    _logger.Warning(
                        ex,
                        "The {ConnectionName:l} API at '{BaseAddress}' answered its Discovery document with something that is not JSON.",
                        _name,
                        _httpClient.BaseAddress
                    );

                    return DiscoveryDocument.Unusable;
                }
            }
            catch (Exception ex)
            {
                // Reaching the API failed outright, which a restart or a transient network fault produces,
                // so this stays the case a later run may resolve on its own.
                _logger.Warning(
                    ex,
                    "The Discovery document for the {ConnectionName:l} API at '{BaseAddress}' could not be read.",
                    _name,
                    baseAddress
                );

                return DiscoveryDocument.Unread;
            }
        }

        /// <summary>
        /// Says whether a status the API answered with could be answered differently by a later run, which
        /// decides whether the run reports a configuration fault or one that may be repeated.
        /// </summary>
        /// <remarks>
        /// Deliberately wider than <c>HttpStatusCodeExtensions.IsPotentiallyTransientFailure</c>, which
        /// answers a different question, namely whether to retry one request inside this run. That one is
        /// left alone because the request retry policy is built on it.
        /// </remarks>
        private static bool MayAnswerDifferentlyLater(HttpStatusCode statusCode) =>
            (int)statusCode >= 500
            || statusCode == HttpStatusCode.RequestTimeout
            || statusCode == HttpStatusCode.TooManyRequests;

        private string ResolveApiSegment(ApiUrlSegmentDefinition definition, string statedSegment)
        {
            var resolver = new EdFiApiUrlSegmentResolver(
                _httpClient.BaseAddress,
                _name,
                ConnectionDetails.SchoolYear,
                _logger
            );

            return resolver.Resolve(statedSegment, _discoveryDocument, definition);
        }

        public HttpClient HttpClient => _httpClient;

        public string Name => _name;

        public ApiConnectionDetails ConnectionDetails { get; }

        /// <summary>
        /// Gets the API's Discovery document as it was read while this client was constructed.
        /// </summary>
        public DiscoveryDocument DiscoveryDocument => _discoveryDocument;

        public string DataManagementApiSegment => _dataManagementApiSegment;

        public string ChangeQueriesApiSegment => _changeQueriesApiSegment;

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
