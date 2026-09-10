// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using NUnit.Framework;
using Shouldly;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// Covers the cap on concurrent source requests: what the source API is asked to serve at one time, however
    /// many resources, partitions or pipeline blocks the requests came from.
    /// </summary>
    [TestFixture]
    public class SourceRequestConcurrencyCapTests
    {
        private const string ResourceRelativeUrl = "data/v3/ed-fi/schools";
        private const int RequestCount = 6;
        private const int Cap = 3;

        [Test]
        public async Task A_configured_cap_should_be_the_most_the_source_api_is_asked_to_serve_at_once()
        {
            TestHelpers.InitializeLogging();

            using var transport = new GatedTransport(awaitedConcurrentRequests: Cap);
            using var apiClient = CreateApiClient(transport, maxConcurrentRequests: Cap);

            var requests = SendConcurrently(apiClient, RequestCount);

            // Reaching the cap proves it is not being applied more tightly than it was configured. If it were, this
            // is where the test would time out rather than at the assertions below.
            await transport.WaitForAwaitedConcurrentRequestsAsync();

            transport.ReleaseAll();

            await Task.WhenAll(requests);

            // Every request was served, but never more than the cap of them at the same time
            Assert.That(transport.TotalRequests, Is.EqualTo(RequestCount));
            Assert.That(transport.PeakConcurrentRequests, Is.EqualTo(Cap));
        }

        /// <summary>
        /// The control for the test above: with the option left at its default the requests are not gated at all, so
        /// the transport does observe more than <see cref="Cap" /> of them at once. Without this, a cap that was
        /// silently never installed would look the same as a cap that works.
        /// </summary>
        [Test]
        public async Task With_the_option_left_at_its_default_the_source_api_should_not_be_capped_at_all()
        {
            TestHelpers.InitializeLogging();

            using var transport = new GatedTransport(awaitedConcurrentRequests: RequestCount);
            using var apiClient = CreateApiClient(transport, maxConcurrentRequests: 0);

            var requests = SendConcurrently(apiClient, RequestCount);

            await transport.WaitForAwaitedConcurrentRequestsAsync();

            transport.ReleaseAll();

            await Task.WhenAll(requests);

            Assert.That(transport.PeakConcurrentRequests, Is.EqualTo(RequestCount));
            Assert.That(transport.PeakConcurrentRequests, Is.GreaterThan(Cap));
        }

        [Test]
        public void A_cap_below_one_would_stall_every_request_and_must_be_rejected()
        {
            using var transport = new GatedTransport(awaitedConcurrentRequests: 1);

            Should.Throw<ArgumentOutOfRangeException>(
                () =>
                {
                    _ = new ConcurrentRequestLimitingHandler(transport, maxConcurrentRequests: 0, "Source");
                });
        }

        private static Task[] SendConcurrently(EdFiApiClient apiClient, int requestCount)
        {
            return Enumerable
                .Range(0, requestCount)
                .Select(
                    i => Task.Run(
                        async () =>
                        {
                            using var response = await apiClient.HttpClient.GetAsync(
                                $"{ResourceRelativeUrl}?offset={i}");

                            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                        }))
                .ToArray();
        }

        private static EdFiApiClient CreateApiClient(GatedTransport transport, int maxConcurrentRequests)
        {
            return new EdFiApiClient(
                "TestSource",
                TestHelpers.GetSourceApiConnectionDetails(),
                bearerTokenRefreshMinutes: 60,
                ignoreSslErrors: true,
                transport,
                timeProvider: null,
                new ApiThrottlingPolicy { MaxConcurrentRequests = maxConcurrentRequests });
        }

        /// <summary>
        /// A transport that holds every data request until the test releases it, so that the number of requests the
        /// API has in hand at one time is whatever the pipeline above it allowed through. Token requests are answered
        /// straight away: they are made on the transport directly rather than through the request pipeline, so
        /// holding one would only deadlock the construction of the client.
        /// </summary>
        private sealed class GatedTransport : HttpClientHandler
        {
            private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

            private readonly TaskCompletionSource _awaitedConcurrentRequestsReached =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            private readonly int _awaitedConcurrentRequests;

            private int _concurrentRequests;
            private int _peakConcurrentRequests;
            private int _totalRequests;

            public GatedTransport(int awaitedConcurrentRequests)
            {
                _awaitedConcurrentRequests = awaitedConcurrentRequests;
            }

            public int PeakConcurrentRequests => Volatile.Read(ref _peakConcurrentRequests);

            public int TotalRequests => Volatile.Read(ref _totalRequests);

            public Task WaitForAwaitedConcurrentRequestsAsync() =>
                _awaitedConcurrentRequestsReached.Task.WaitAsync(TimeSpan.FromSeconds(30));

            public void ReleaseAll() => _release.TrySetResult();

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                if (request.Method == HttpMethod.Post)
                {
                    return FakeResponse.OK(new { access_token = "test-access-token" });
                }

                Interlocked.Increment(ref _totalRequests);

                int concurrentRequests = Interlocked.Increment(ref _concurrentRequests);

                RecordPeak(concurrentRequests);

                if (concurrentRequests >= _awaitedConcurrentRequests)
                {
                    _awaitedConcurrentRequestsReached.TrySetResult();
                }

                try
                {
                    await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("[]", Encoding.UTF8, "application/json")
                    };
                }
                finally
                {
                    Interlocked.Decrement(ref _concurrentRequests);
                }
            }

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
