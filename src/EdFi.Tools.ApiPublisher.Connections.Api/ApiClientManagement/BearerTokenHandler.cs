// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using Serilog;

namespace EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement
{
    /// <summary>
    /// Applies the current bearer token to every outgoing request and, when the API rejects a request as
    /// unauthorized, re-acquires the token once and replays the request. Handling this in the request pipeline
    /// covers every call made through the client, including the calls that have no retry policy of their own.
    /// </summary>
    public class BearerTokenHandler : DelegatingHandler
    {
        private readonly IBearerTokenProvider _bearerTokenProvider;
        private readonly string _name;
        private readonly ILogger _logger = Log.ForContext(typeof(BearerTokenHandler));

        public BearerTokenHandler(
            HttpMessageHandler innerHandler,
            IBearerTokenProvider bearerTokenProvider,
            string name
        )
            : base(innerHandler)
        {
            _bearerTokenProvider =
                bearerTokenProvider ?? throw new ArgumentNullException(nameof(bearerTokenProvider));
            _name = name;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (_bearerTokenProvider.IsAuthenticationFailed)
            {
                throw new EdFiApiAuthenticationException(
                    $"The bearer token for the {_name.ToLower()} API client could not be obtained, so the request '{request.Method} {request.RequestUri}' cannot be authenticated."
                );
            }

            string bearerToken = _bearerTokenProvider.CurrentBearerToken;

            ApplyBearerToken(request, bearerToken);

            // The body and its headers have to be captured before sending, because the content is disposed once the
            // request has been sent. Everything else on the request remains readable afterwards.
            byte[] requestBody = request.Content == null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            var requestContentHeaders = request.Content?.Headers.ToList();

            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                return response;
            }

            _logger.Warning(
                "'{Method} {RequestUri}' was rejected as unauthorized by the {Name} API. Re-acquiring the bearer token and replaying the request...",
                request.Method,
                request.RequestUri,
                _name.ToLower()
            );

            bool tokenIsUsable = await _bearerTokenProvider
                .TryReacquireBearerTokenAsync(bearerToken, cancellationToken)
                .ConfigureAwait(false);

            if (!tokenIsUsable)
            {
                // Leave the unauthorized response to the caller. The provider has already reported the failure, and
                // it fails subsequent requests outright once the token can no longer be obtained at all.
                return response;
            }

            var replayRequest = CloneRequest(request, requestBody, requestContentHeaders);

            ApplyBearerToken(replayRequest, _bearerTokenProvider.CurrentBearerToken);

            response.Dispose();

            return await base.SendAsync(replayRequest, cancellationToken).ConfigureAwait(false);
        }

        private static void ApplyBearerToken(HttpRequestMessage request, string bearerToken)
        {
            if (!string.IsNullOrEmpty(bearerToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            }
        }

        private static HttpRequestMessage CloneRequest(
            HttpRequestMessage request,
            byte[] requestBody,
            List<KeyValuePair<string, IEnumerable<string>>> requestContentHeaders
        )
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Version = request.Version,
                VersionPolicy = request.VersionPolicy
            };

            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            foreach (var option in (IDictionary<string, object>)request.Options)
            {
                clone.Options.Set(new HttpRequestOptionsKey<object>(option.Key), option.Value);
            }

            if (requestBody != null)
            {
                clone.Content = new ByteArrayContent(requestBody);

                clone.Content.Headers.Clear();

                foreach (var contentHeader in requestContentHeaders)
                {
                    clone.Content.Headers.TryAddWithoutValidation(contentHeader.Key, contentHeader.Value);
                }
            }

            return clone;
        }
    }
}
