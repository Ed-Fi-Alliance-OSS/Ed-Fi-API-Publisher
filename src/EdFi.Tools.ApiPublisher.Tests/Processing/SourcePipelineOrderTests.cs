// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// Covers the two throttling handlers working together, which is the arrangement the publisher actually
    /// ships: every source client is built with both a 429 wait and, where one is configured, a cap on concurrent
    /// requests. The other two fixtures each exercise one handler on its own, so nothing there would notice the
    /// pair being wired in the wrong order.
    /// </summary>
    [TestFixture]
    public class SourcePipelineOrderTests
    {
        private const string RejectedRelativeUrl = "data/v3/ed-fi/schools";
        private const string OtherRelativeUrl = "data/v3/ed-fi/staffs";

        /// <summary>
        /// The wait for a rejected read has to happen outside the cap, or a source that rejects one read would
        /// stall the reads that the cap would otherwise let through. With a cap of one, this is exact: while the
        /// rejected read is waiting, the single slot must be free for another read to be served.
        /// </summary>
        [Test]
        public async Task A_read_waiting_out_a_rejection_should_not_be_holding_a_concurrency_slot()
        {
            TestHelpers.InitializeLogging();

            var clock = new FakeTimeProvider();

            using var transport = new RejectingCountingTransport(RejectedRelativeUrl);
            using var apiClient = CreateApiClient(transport, clock);

            // Waits out a minute, which the request budget below can cover, so it is a wait and not an abandonment
            var rejectedRead = apiClient.HttpClient.GetAsync(RejectedRelativeUrl);

            await transport.WaitForFirstRejectionAsync();

            // The rejected read is now waiting. If that wait were holding the only slot, this could never be
            // served, and the wait below would expire instead. No clock stepping, so nothing else can unblock it.
            using var otherResponse = await apiClient
                .HttpClient.GetAsync(OtherRelativeUrl)
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.That(otherResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            await clock.AdvanceUntilCompletedAsync(rejectedRead, TimeSpan.FromSeconds(1));

            using var rejectedResponse = await rejectedRead;

            Assert.That(rejectedResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // And the cap still held throughout, so letting the wait out of the slot did not let the cap slip
            Assert.That(transport.PeakConcurrentRequests, Is.EqualTo(1));
        }

        private static EdFiApiClient CreateApiClient(HttpClientHandler transport, TimeProvider timeProvider)
        {
            return new EdFiApiClient(
                "TestSource",
                TestHelpers.GetSourceApiConnectionDetails(),
                bearerTokenRefreshMinutes: 60,
                ignoreSslErrors: true,
                transport,
                timeProvider,
                new ApiThrottlingPolicy
                {
                    MaxConcurrentRequests = 1,
                    TooManyRequestsRetryAttempts = 2,
                    TooManyRequestsRetryStartingDelay = TimeSpan.FromSeconds(1),
                    RequestBudget = TimeSpan.FromMinutes(10),
                });
        }

        /// <summary>
        /// Rejects the first read of one resource with a 429 and serves everything else, counting how many reads
        /// it has in hand at once.
        /// </summary>
        private sealed class RejectingCountingTransport : HttpClientHandler
        {
            private readonly TaskCompletionSource _firstRejectionSent =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            private readonly ConcurrentDictionary<string, byte> _alreadyRejected = new();
            private readonly string _resourceToReject;

            private int _concurrentRequests;
            private int _peakConcurrentRequests;

            public RejectingCountingTransport(string resourceToReject)
            {
                _resourceToReject = resourceToReject;
            }

            public int PeakConcurrentRequests => Volatile.Read(ref _peakConcurrentRequests);

            public Task WaitForFirstRejectionAsync() =>
                _firstRejectionSent.Task.WaitAsync(TimeSpan.FromSeconds(30));

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                if (request.RequestUri.AbsolutePath.EndsWith("/oauth/token"))
                {
                    return Task.FromResult(FakeResponse.OK(new { access_token = "test-access-token" }));
                }

                int concurrentRequests = Interlocked.Increment(ref _concurrentRequests);

                RecordPeak(concurrentRequests);

                try
                {
                    bool reject =
                        request.RequestUri.AbsolutePath.EndsWith(_resourceToReject)
                        && _alreadyRejected.TryAdd(_resourceToReject, 0);

                    if (!reject)
                    {
                        return Task.FromResult(Ok());
                    }

                    var rejection = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                    {
                        Content = new StringContent("{}", Encoding.UTF8, "application/json")
                    };

                    rejection.Headers.TryAddWithoutValidation("Retry-After", "60");

                    _firstRejectionSent.TrySetResult();

                    return Task.FromResult(rejection);
                }
                finally
                {
                    Interlocked.Decrement(ref _concurrentRequests);
                }
            }

            private static HttpResponseMessage Ok() =>
                new(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json")
                };

            private void RecordPeak(int concurrentRequests)
            {
                int peak = Volatile.Read(ref _peakConcurrentRequests);

                while (concurrentRequests > peak)
                {
                    int previousPeak = Interlocked.CompareExchange(
                        ref _peakConcurrentRequests,
                        concurrentRequests,
                        peak);

                    if (previousPeak == peak)
                    {
                        return;
                    }

                    peak = previousPeak;
                }
            }
        }
    }
}
