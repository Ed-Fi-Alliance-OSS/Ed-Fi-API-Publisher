// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using Serilog;

namespace EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement
{
    /// <summary>
    /// Waits out and replays a read the API rejected with 429 Too Many Requests, for as long as the configured
    /// number of retries allows. Handling this in the request pipeline covers every read made through the client,
    /// including the ones that have no retry policy of their own, and it keeps the wait off the retry policies that
    /// are shared with writes to the target API: a target that reports it is being written to too quickly is left to
    /// the caller exactly as it is today.
    /// </summary>
    /// <remarks>
    /// Only reads are replayed. A write is left to its caller, both because replaying one means reasoning about
    /// whether the API applied it and because scoping the 429 handling to reads was the decision recorded in the
    /// APIPUB-136 spike.
    /// </remarks>
    public class RetryAfterHandler : DelegatingHandler
    {
        private readonly int _maxRetryAttempts;
        private readonly TimeSpan _retryStartingDelay;
        private readonly TimeProvider _timeProvider;
        private readonly string _displayName;
        private readonly ILogger _logger = Log.ForContext(typeof(RetryAfterHandler));

        public RetryAfterHandler(
            HttpMessageHandler innerHandler,
            ApiThrottlingPolicy throttlingPolicy,
            string name,
            TimeProvider timeProvider = null
        )
            : base(innerHandler)
        {
            ArgumentNullException.ThrowIfNull(throttlingPolicy);

            if (throttlingPolicy.MaxRetryAttempts < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(throttlingPolicy),
                    throttlingPolicy.MaxRetryAttempts,
                    "A client that is not meant to retry a rejected read should be built without this handler."
                );
            }

            _maxRetryAttempts = throttlingPolicy.MaxRetryAttempts;
            _retryStartingDelay = throttlingPolicy.RetryStartingDelay;
            _timeProvider = timeProvider ?? TimeProvider.System;
            _displayName = name?.ToLower();
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.TooManyRequests || request.Method != HttpMethod.Get)
            {
                return response;
            }

            for (int retryAttempt = 1; retryAttempt <= _maxRetryAttempts; retryAttempt++)
            {
                var delay = GetRetryDelay(response, retryAttempt);

                _logger.Warning(
                    "'{Method} {RequestUri}' was rejected as too many requests by the {Name} API. Waiting {TotalSeconds:N1}s before retrying... (retry #{RetryAttempt} of {MaxRetryAttempts})",
                    request.Method,
                    request.RequestUri,
                    _displayName,
                    delay.TotalSeconds,
                    retryAttempt,
                    _maxRetryAttempts
                );

                // The rejected response holds a live connection open until it is disposed (see APIPUB-134), and
                // nothing downstream is going to read a response that is about to be replaced.
                response.Dispose();

                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);

                // A request that has been sent cannot be sent again, and the clone is let go of as soon as the
                // replay has been sent. Only reads reach this, so there is no body to carry over.
                using (var retryRequest = CloneRequestWithoutContent(request))
                {
                    response = await base.SendAsync(retryRequest, cancellationToken).ConfigureAwait(false);
                }

                if (response.StatusCode != HttpStatusCode.TooManyRequests)
                {
                    return response;
                }
            }

            _logger.Error(
                "'{Method} {RequestUri}' was still being rejected as too many requests by the {Name} API after {MaxRetryAttempts} retries. The rejection is reported to the caller.",
                request.Method,
                request.RequestUri,
                _displayName,
                _maxRetryAttempts
            );

            // Reporting the rejection puts the read on the same failure path a read that kept failing with 503
            // takes today: the caller records the failure and overall processing is forced to fail.
            return response;
        }

        /// <summary>
        /// How long to wait before the next attempt: the exponential back off from the configured starting delay,
        /// except where the API asked to be left alone for longer than that.
        /// </summary>
        private TimeSpan GetRetryDelay(HttpResponseMessage response, int retryAttempt)
        {
            var backoffDelay = _retryStartingDelay * Math.Pow(2, retryAttempt - 1);

            var requestedDelay = GetRequestedDelay(response);

            return requestedDelay > backoffDelay ? requestedDelay.Value : backoffDelay;
        }

        /// <summary>
        /// The wait the API asked for, as either a number of seconds or the date it will start answering again. A
        /// header that is absent, unparsable or already in the past leaves the back off to decide.
        /// </summary>
        private TimeSpan? GetRequestedDelay(HttpResponseMessage response)
        {
            // An unparsable header value is not surfaced here at all: it stays among the response's invalid headers
            var retryAfter = response.Headers.RetryAfter;

            if (retryAfter is null)
            {
                return null;
            }

            if (retryAfter.Delta is not null)
            {
                return retryAfter.Delta;
            }

            if (retryAfter.Date is not null)
            {
                return retryAfter.Date.Value - _timeProvider.GetUtcNow();
            }

            return null;
        }

        private static HttpRequestMessage CloneRequestWithoutContent(HttpRequestMessage request)
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

            return clone;
        }
    }
}
