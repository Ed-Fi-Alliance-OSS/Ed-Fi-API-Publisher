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
        private readonly string _displayName;
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
            _displayName = name?.ToLower();
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (_bearerTokenProvider.IsAuthenticationFailed)
            {
                throw new EdFiApiAuthenticationException(
                    $"The bearer token for the {_displayName} API client could not be obtained, so the request '{request.Method} {request.RequestUri}' cannot be authenticated."
                );
            }

            string bearerToken = _bearerTokenProvider.CurrentBearerToken;

            ApplyBearerToken(request, bearerToken);

            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                return response;
            }

            _logger.Warning(
                "'{Method} {RequestUri}' was rejected as unauthorized by the {Name} API. Re-acquiring the bearer token and replaying the request...",
                request.Method,
                request.RequestUri,
                _displayName
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

            var replayRequest = await TryCloneRequestAsync(request, cancellationToken).ConfigureAwait(false);

            if (replayRequest == null)
            {
                return response;
            }

            ApplyBearerToken(replayRequest, _bearerTokenProvider.CurrentBearerToken);

            response.Dispose();

            return await base.SendAsync(replayRequest, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Builds the request to replay. The body is only read here, on the unauthorized path, so that an ordinary
        /// request does not pay for a copy it never needs. It is still readable at this point because the request is
        /// disposed by <see cref="HttpClient" /> once the send completes, which is after this handler returns.
        /// </summary>
        private async Task<HttpRequestMessage> TryCloneRequestAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (request.Content == null)
            {
                return CloneRequest(request, requestBody: null, requestContentHeaders: null);
            }

            long? expectedLength = request.Content.Headers.ContentLength;
            byte[] requestBody;

            try
            {
                requestBody = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or IOException)
            {
                _logger.Warning(
                    ex,
                    "'{Method} {RequestUri}' cannot be replayed because its body is no longer readable. The unauthorized response is reported instead.",
                    request.Method,
                    request.RequestUri
                );

                return null;
            }

            // A body that is no longer fully readable would be replayed truncated, which is worse than reporting
            // the unauthorized response.
            if (expectedLength != null && requestBody.LongLength != expectedLength.Value)
            {
                _logger.Warning(
                    "'{Method} {RequestUri}' cannot be replayed because only {ActualLength} of {ExpectedLength} bytes of its body are still readable. The unauthorized response is reported instead.",
                    request.Method,
                    request.RequestUri,
                    requestBody.LongLength,
                    expectedLength.Value
                );

                return null;
            }

            return CloneRequest(request, requestBody, request.Content.Headers.ToList());
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
