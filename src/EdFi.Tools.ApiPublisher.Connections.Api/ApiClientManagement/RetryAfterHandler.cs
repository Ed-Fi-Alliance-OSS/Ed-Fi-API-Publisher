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
    /// <para>
    /// Only reads are replayed. A write is left to its caller, both because replaying one means reasoning about
    /// whether the API applied it and because scoping the 429 handling to reads was the decision recorded in the
    /// APIPUB-136 spike.
    /// </para>
    /// <para>
    /// The wait happens inside the caller's request, so every wait is spent against
    /// <see cref="ApiThrottlingPolicy.RequestBudget" />, which the client applies as its
    /// <see cref="HttpClient.Timeout" />. A wait the remaining budget cannot cover is not started: the read is
    /// abandoned and the rejection is reported, because letting the budget expire mid-wait would surface the
    /// rejection to the caller as a cancelled request with no status rather than as the 429 it is.
    /// </para>
    /// </remarks>
    public class RetryAfterHandler : DelegatingHandler
    {
        /// <summary>
        /// The most of the request budget ever held back for a replay.
        /// </summary>
        private static readonly TimeSpan MaxReplayAllowance = TimeSpan.FromSeconds(10);

        private readonly int _maxRetryAttempts;
        private readonly TimeSpan _retryStartingDelay;
        private readonly TimeSpan _requestBudget;
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

            if (throttlingPolicy.TooManyRequestsRetryAttempts < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(throttlingPolicy),
                    throttlingPolicy.TooManyRequestsRetryAttempts,
                    "A client that is not meant to retry a rejected read should be built without this handler."
                );
            }

            _maxRetryAttempts = throttlingPolicy.TooManyRequestsRetryAttempts;
            _retryStartingDelay = throttlingPolicy.TooManyRequestsRetryStartingDelay;
            _requestBudget = throttlingPolicy.RequestBudget;
            _timeProvider = timeProvider ?? TimeProvider.System;
            _displayName = name?.ToLower();
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            long startedAt = _timeProvider.GetTimestamp();

            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.TooManyRequests || request.Method != HttpMethod.Get)
            {
                return response;
            }

            for (int retryAttempt = 1; retryAttempt <= _maxRetryAttempts; retryAttempt++)
            {
                var (delay, delaySource) = GetRetryDelay(response, retryAttempt);

                var remainingBudget = _requestBudget - _timeProvider.GetElapsedTime(startedAt);

                // The replay has to fit in what is left after the wait, or the budget expires during the replay
                // and the caller sees a cancelled request with no status, which is the outcome this guard exists
                // to avoid. Reserving nothing was not enough: a wait ending a second before the budget does
                // leaves a slow source no room to answer.
                if (delay + ReplayAllowance(_requestBudget) >= remainingBudget)
                {
                    LogWaitDoesNotFitTheBudget(request, delay, delaySource, remainingBudget);

                    return response;
                }

                _logger.Warning(
                    "'{Method:l} {RequestUri}' was rejected as too many requests by the {Name:l} API. Waiting {TotalSeconds:N1}s ({DelaySource:l}) before retrying... (retry #{RetryAttempt} of {MaxRetryAttempts})",
                    request.Method.Method,
                    request.RequestUri,
                    _displayName,
                    delay.TotalSeconds,
                    delaySource,
                    retryAttempt,
                    _maxRetryAttempts
                );

                // The rejected response holds a live connection open until it is disposed (see APIPUB-134), and
                // nothing downstream is going to read a response that is about to be replaced.
                response.Dispose();

                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);

                // A request that has been sent cannot be sent again, and the clone is let go of as soon as the
                // replay has been sent. Only reads reach this, so there is no body to carry over. The stale
                // Authorization header the clone copies is replaced downstream by the bearer token handler.
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
                "'{Method:l} {RequestUri}' was still being rejected as too many requests by the {Name:l} API after {MaxRetryAttempts} retries. The read is recorded as a failure and the run will finish with errors. Consider lowering MaxConcurrentSourceRequests, or the MaxDegreeOfParallelism settings, so the source is asked for less at once, or raising TooManyRequestsRetryAttempts.",
                request.Method.Method,
                request.RequestUri,
                _displayName,
                _maxRetryAttempts
            );

            // Reporting the rejection puts the read on the same failure path a read that kept failing with 503
            // takes today: the caller records the failure and overall processing is forced to fail.
            return response;
        }

        private void LogWaitDoesNotFitTheBudget(
            HttpRequestMessage request,
            TimeSpan delay,
            string delaySource,
            TimeSpan remainingBudget
        )
        {
            _logger.Error(
                "'{Method:l} {RequestUri}' was rejected as too many requests by the {Name:l} API, which asked for a wait of {TotalSeconds:N1}s ({DelaySource:l}). Only {RemainingSeconds:N1}s of the {BudgetSeconds:N0}s request budget is left, so the read is abandoned and recorded as a failure rather than being cut short mid-wait. Consider lowering MaxConcurrentSourceRequests, or the MaxDegreeOfParallelism settings, so the source is asked for less at once.",
                request.Method.Method,
                request.RequestUri,
                _displayName,
                delay.TotalSeconds,
                delaySource,
                remainingBudget.TotalSeconds,
                _requestBudget.TotalSeconds
            );
        }

        /// <summary>
        /// How long to wait before the next attempt, and where that came from: the exponential back off from the
        /// configured starting delay, except where the API asked to be left alone for longer than that.
        /// </summary>
        private (TimeSpan Delay, string Source) GetRetryDelay(HttpResponseMessage response, int retryAttempt)
        {
            var backoffDelay = GetBackoffDelay(retryAttempt);

            var requestedDelay = GetRequestedDelay(response);

            return requestedDelay > backoffDelay
                ? (requestedDelay.Value, "Retry-After")
                : (backoffDelay, "back off");
        }

        /// <summary>
        /// How much of the request budget is held back for the replay itself, so that a wait is only started when
        /// the source still has time to answer afterwards. Ten seconds covers an ordinary page read from a busy
        /// API, and the quarter-of-the-budget ceiling keeps the allowance sensible for a client configured with a
        /// short budget rather than refusing every wait.
        /// </summary>
        private static TimeSpan ReplayAllowance(TimeSpan requestBudget) =>
            TimeSpan.FromTicks(Math.Min(MaxReplayAllowance.Ticks, requestBudget.Ticks / 4));

        /// <summary>
        /// The exponential back off for this attempt, saturated at the request budget so that a large attempt
        /// count cannot overflow the multiplication. Any value at or above the budget is rejected by the caller
        /// anyway, so saturating loses nothing.
        /// </summary>
        private TimeSpan GetBackoffDelay(int retryAttempt)
        {
            double ticks = _retryStartingDelay.Ticks * Math.Pow(2, retryAttempt - 1);

            return ticks >= _requestBudget.Ticks ? _requestBudget : TimeSpan.FromTicks((long)ticks);
        }

        /// <summary>
        /// The wait the API asked for, as either a number of seconds or the date it will start answering again. A
        /// header that is absent, unparsable or already in the past leaves the back off to decide.
        /// </summary>
        private TimeSpan? GetRequestedDelay(HttpResponseMessage response)
        {
            // A value the framework could not parse is not surfaced here at all: it stays among the response's
            // invalid headers, so it is reported rather than silently treated as no header having been sent.
            var retryAfter = response.Headers.RetryAfter;

            if (retryAfter is null)
            {
                LogUnreadableRetryAfter(response);

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

        private void LogUnreadableRetryAfter(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Retry-After", out var rawValues))
            {
                return;
            }

            _logger.Warning(
                "The {Name:l} API sent a Retry-After value that could not be read ('{RetryAfter:l}'), so the configured back off is used instead.",
                _displayName,
                string.Join(", ", rawValues)
            );
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
