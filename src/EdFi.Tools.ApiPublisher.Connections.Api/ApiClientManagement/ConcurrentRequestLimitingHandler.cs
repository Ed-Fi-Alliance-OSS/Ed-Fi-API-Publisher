// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Serilog;

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
        private readonly SemaphoreSlim _availableSlots;

        public ConcurrentRequestLimitingHandler(
            HttpMessageHandler innerHandler,
            int maxConcurrentRequests,
            string name
        )
            : base(innerHandler)
        {
            if (maxConcurrentRequests < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxConcurrentRequests),
                    maxConcurrentRequests,
                    "The cap on concurrent requests must be greater than 0. A client that is not meant to be capped should be built without this handler."
                );
            }

            _availableSlots = new SemaphoreSlim(maxConcurrentRequests, maxConcurrentRequests);

            Log.ForContext(typeof(ConcurrentRequestLimitingHandler))
                .Information(
                    "Requests to the {Name:l} API are capped at {MaxConcurrentRequests} concurrent. Reads waiting for a slot are the cap working, not a hang.",
                    name?.ToLower(),
                    maxConcurrentRequests
                );
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            await _availableSlots.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _availableSlots.Release();
            }
        }

        // No Dispose override: the client builds its pipeline with disposeHandler false and disposes the transport
        // itself, so a handler here is never disposed and an override would only look like it ran. The semaphore
        // needs no disposal either, because that matters only once AvailableWaitHandle has been asked for, and
        // nothing here asks for it.
    }
}
