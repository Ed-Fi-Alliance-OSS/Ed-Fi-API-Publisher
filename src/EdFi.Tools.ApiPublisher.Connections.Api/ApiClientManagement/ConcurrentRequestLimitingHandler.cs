// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using Serilog;
using Serilog.Events;

namespace EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement
{
    /// <summary>
    /// Caps the number of requests the client has in flight against its API at any one time. The cap applies to
    /// the client as a whole, so it bounds what one API host is asked to serve at once no matter how the work that
    /// produced the requests was divided up between resources, partitions or pipeline blocks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A request holds its slot until the handler chain returns, which is when the response headers have been
    /// received. The body is streamed after that and is not counted, so the cap bounds requests the API is still
    /// producing a response for rather than bytes in flight. Bearer token requests are not counted either: the
    /// token manager sends those on the transport directly rather than through this pipeline.
    /// </para>
    /// <para>
    /// Waiting for a slot happens inside the caller's request, so it is spent against the client's
    /// <see cref="HttpClient.Timeout" />. A cap far below the parallelism the pipeline offers will therefore
    /// surface as cancelled requests rather than as slower ones, which is why the publisher warns at startup when
    /// the two are that far apart.
    /// </para>
    /// </remarks>
    public class ConcurrentRequestLimitingHandler : DelegatingHandler
    {
        /// <summary>
        /// How short of the whole request budget a wait may fall and still be read as the request having run out
        /// of time. A cancellation that arrives earlier than this came from outside the request, so it is reported
        /// as what it is rather than as the cap being too low.
        /// </summary>
        private static readonly TimeSpan BudgetShortfallAllowance = TimeSpan.FromSeconds(1);

        private readonly SemaphoreSlim _availableSlots;
        private readonly int _maxConcurrentRequests;
        private readonly TimeSpan _requestBudget;
        private readonly string _displayName;
        private readonly ILogger _logger = Log.ForContext(typeof(ConcurrentRequestLimitingHandler));

        public ConcurrentRequestLimitingHandler(
            HttpMessageHandler innerHandler,
            ApiThrottlingPolicy throttlingPolicy,
            string name
        )
            : base(innerHandler)
        {
            ArgumentNullException.ThrowIfNull(throttlingPolicy);

            if (throttlingPolicy.MaxConcurrentRequests < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(throttlingPolicy),
                    throttlingPolicy.MaxConcurrentRequests,
                    "The cap on concurrent requests must be greater than 0. A client that is not meant to be capped should be built without this handler."
                );
            }

            _availableSlots = new SemaphoreSlim(
                throttlingPolicy.MaxConcurrentRequests,
                throttlingPolicy.MaxConcurrentRequests
            );

            _maxConcurrentRequests = throttlingPolicy.MaxConcurrentRequests;
            _requestBudget = throttlingPolicy.RequestBudget;
            _displayName = name?.ToLower();
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            await WaitForSlotAsync(request, cancellationToken).ConfigureAwait(false);

            try
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _availableSlots.Release();
            }
        }

        /// <summary>
        /// Takes a slot, and reports it if the request does not live long enough to get one. The wait is spent
        /// against the request's own budget, so a cap well below the parallelism the pipeline offers will run some
        /// requests out of time while they queue. That surfaces to the caller as a cancellation naming the HTTP
        /// client and nothing else, which is why the cap has to name itself here.
        /// </summary>
        private async Task WaitForSlotAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var slot = _availableSlots.WaitAsync(cancellationToken);

            if (slot.IsCompletedSuccessfully)
            {
                return;
            }

            long startedWaitingAt = Stopwatch.GetTimestamp();

            // Reported from the token rather than from around the await, so that the line is written at the moment
            // the wait is given up on and the cancellation itself reaches the caller untouched. The registration is
            // released as soon as the slot arrives, so a request that is served never logs this.
            using (cancellationToken.Register(() => LogSlotWaitAbandoned(request, startedWaitingAt)))
            {
                await slot.ConfigureAwait(false);
            }

            if (_logger.IsEnabled(LogEventLevel.Debug))
            {
                _logger.Debug(
                    "'{Method:l} {RequestUri}' waited {TotalSeconds:N1}s for one of the {MaxConcurrentRequests} concurrent slots allowed against the {Name:l} API.",
                    request.Method.Method,
                    request.RequestUri,
                    Stopwatch.GetElapsedTime(startedWaitingAt).TotalSeconds,
                    _maxConcurrentRequests,
                    _displayName
                );
            }
        }

        /// <summary>
        /// Names the cap as the thing the request was waiting on. Without this the caller sees only a cancellation
        /// naming the HTTP client, and nothing connects the failure back to the setting that caused it.
        /// </summary>
        /// <remarks>
        /// Only a wait that used up what the request had is reported as a failure of the cap. A wait that ended
        /// with time still on the clock was ended from outside the request, by a resource whose processing was
        /// cancelled or by the run being stopped, and telling an operator to change the cap for that is wrong: a
        /// stopped run with a deep queue would say it once per queued read. Where the request reached the cap with
        /// most of its budget already spent waiting out a rejection above, the abandonment is recorded at Debug
        /// rather than Error, which under-reports rather than misdirects.
        /// </remarks>
        private void LogSlotWaitAbandoned(HttpRequestMessage request, long startedWaitingAt)
        {
            if (Stopwatch.GetElapsedTime(startedWaitingAt) + BudgetShortfallAllowance < _requestBudget)
            {
                _logger.Debug(
                    "'{Method:l} {RequestUri}' was cancelled after waiting {TotalSeconds:N1}s for one of the {MaxConcurrentRequests} concurrent slots allowed against the {Name:l} API.",
                    request.Method.Method,
                    request.RequestUri,
                    Stopwatch.GetElapsedTime(startedWaitingAt).TotalSeconds,
                    _maxConcurrentRequests,
                    _displayName
                );

                return;
            }

            _logger.Error(
                "'{Method:l} {RequestUri}' ran out of the time it is allowed after waiting {TotalSeconds:N1}s for one of the {MaxConcurrentRequests} concurrent slots allowed against the {Name:l} API. The cap is lower than the parallelism settings can keep busy: lower MaxDegreeOfParallelismForResourceProcessing and MaxDegreeOfParallelismForStreamResourcePages to match it, or raise the cap.",
                request.Method.Method,
                request.RequestUri,
                Stopwatch.GetElapsedTime(startedWaitingAt).TotalSeconds,
                _maxConcurrentRequests,
                _displayName
            );
        }

        // No Dispose override: the client builds its pipeline with disposeHandler false and disposes the transport
        // itself, so a handler here is never disposed and an override would only look like it ran. The semaphore
        // needs no disposal either, because that matters only once AvailableWaitHandle has been asked for, and
        // nothing here asks for it.
    }
}
