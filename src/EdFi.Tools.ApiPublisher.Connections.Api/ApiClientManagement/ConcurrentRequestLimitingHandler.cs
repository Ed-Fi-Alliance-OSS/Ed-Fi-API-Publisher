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
    /// A request holds its slot from the moment it is sent until its response headers have been received, which is
    /// the window the API spends producing the response. Reading the response body happens after the slot has been
    /// released, because the client reads bodies with
    /// <see cref="HttpCompletionOption.ResponseHeadersRead" /> and a body that is still being consumed is no longer
    /// work the API is waiting on.
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
                    "Requests to the {Name} API are capped at {MaxConcurrentRequests} concurrent.",
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

        protected override void Dispose(bool disposing)
        {
            // The inner handler is disposed first, so nothing can still be waiting on a slot by the time the
            // semaphore itself goes away.
            base.Dispose(disposing);

            if (disposing)
            {
                _availableSlots.Dispose();
            }
        }
    }
}
