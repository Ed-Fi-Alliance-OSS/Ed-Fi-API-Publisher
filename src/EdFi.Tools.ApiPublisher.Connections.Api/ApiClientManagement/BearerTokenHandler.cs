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
    /// unauthorized, re-acquires the token and replays the request. Handling this in the request pipeline covers
    /// every call made through the client, including the calls that have no retry policy of their own. When the
    /// token cannot be re-acquired at all, the request fails with <see cref="EdFiApiAuthenticationException" />
    /// rather than with the unauthorized response, which a caller would record as an ordinary failure of that one
    /// request and move on from.
    /// </summary>
    public class BearerTokenHandler : DelegatingHandler
    {
        /// <summary>
        /// The largest request body that is buffered for a replay. Well above any Ed-Fi resource document, and low
        /// enough that a burst of rejected requests cannot hold an unbounded amount of memory. A larger body is not
        /// replayed and the unauthorized response is reported for it instead.
        /// </summary>
        public const long MaxReplayableBodyLength = 16 * 1024 * 1024;

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

            HttpRequestMessage replayRequest;

            try
            {
                bool tokenIsUsable = await _bearerTokenProvider
                    .TryReacquireBearerTokenAsync(bearerToken, cancellationToken)
                    .ConfigureAwait(false);

                if (!tokenIsUsable)
                {
                    // The provider has retried the re-acquisition for as long as its policy allows and has given up,
                    // so the token is not coming back. The provider has already reported that; this only has to keep
                    // the failure from being mistaken for an unauthorized response to this one request.
                    throw new EdFiApiAuthenticationException(
                        $"The bearer token for the {_displayName} API client could not be re-acquired after '{request.Method} {request.RequestUri}' was rejected as unauthorized, so the request cannot be authenticated."
                    );
                }

                replayRequest = await TryCloneRequestAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Whether the re-acquisition was given up on or interrupted by cancellation, the unauthorized response
                // is not going to anyone.
                response.Dispose();

                throw;
            }

            if (replayRequest is null)
            {
                return response;
            }

            // The clone and its buffered body are let go of as soon as the replay has been sent. Nothing downstream
            // reads the request back off the response.
            using (replayRequest)
            {
                ApplyBearerToken(replayRequest, _bearerTokenProvider.CurrentBearerToken);

                response.Dispose();

                return await base.SendAsync(replayRequest, cancellationToken).ConfigureAwait(false);
            }
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
            if (request.Content is null)
            {
                return CloneRequest(request, requestBody: null, requestContentHeaders: null);
            }

            long? expectedLength = request.Content.Headers.ContentLength;

            if (expectedLength > MaxReplayableBodyLength)
            {
                LogBodyTooLargeToReplay(request, expectedLength.Value);

                return null;
            }

            byte[] requestBody;

            try
            {
                // Buffered under a limit, so that a body whose length was not declared cannot grow the copy without
                // bound either. The limit is enforced by the buffering itself.
                await request
                    .Content.LoadIntoBufferAsync(MaxReplayableBodyLength, cancellationToken)
                    .ConfigureAwait(false);

                requestBody = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                // This is how the buffering reports that the limit was exceeded
                LogBodyTooLargeToReplay(request, ex);

                return null;
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
            if (expectedLength is not null && requestBody.LongLength != expectedLength.Value)
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

        private void LogBodyTooLargeToReplay(HttpRequestMessage request, long declaredLength) =>
            _logger.Warning(
                "'{Method} {RequestUri}' cannot be replayed because its body of {DeclaredLength} bytes exceeds the {MaxLength} bytes that are buffered for a replay. The unauthorized response is reported instead.",
                request.Method,
                request.RequestUri,
                declaredLength,
                MaxReplayableBodyLength
            );

        private void LogBodyTooLargeToReplay(HttpRequestMessage request, Exception exception) =>
            _logger.Warning(
                exception,
                "'{Method} {RequestUri}' cannot be replayed because its body exceeds the {MaxLength} bytes that are buffered for a replay. The unauthorized response is reported instead.",
                request.Method,
                request.RequestUri,
                MaxReplayableBodyLength
            );

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

            if (requestBody is not null)
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
