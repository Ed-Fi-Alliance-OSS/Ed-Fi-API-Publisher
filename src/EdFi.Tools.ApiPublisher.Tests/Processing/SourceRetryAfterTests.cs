// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// Covers what the client does with a source read the API rejects as too many requests: how long it waits before
    /// replaying it, what it falls back on when the API does not say, and what the caller ends up seeing when the
    /// API never stops rejecting it.
    /// </summary>
    [TestFixture]
    public class SourceRetryAfterTests
    {
        private const string TokenUrl = MockRequests.SourceApiBaseUrl + "/oauth/token";
        private const string ResourceUrl = MockRequests.SourceApiBaseUrl + "/data/v3/ed-fi/schools";
        private const string ResourceRelativeUrl = "data/v3/ed-fi/schools";
        private const int MaxRetryAttempts = 2;

        private static readonly TimeSpan RetryStartingDelay = TimeSpan.FromMilliseconds(250);

        [Test]
        public async Task A_read_rejected_with_a_retry_after_in_seconds_should_not_be_replayed_before_that_much_time_has_passed()
        {
            var fakeRequestHandler = GivenASourceApi();

            var rejectedContent = new InstrumentedJsonContent(@"{""message"":""too many requests""}");

            A.CallTo(() => fakeRequestHandler.Get(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(() => TooManyRequests("2", rejectedContent))
                .Once()
                .Then.ReturnsLazily(() => Ok());

            var clock = new FakeTimeProvider();

            using var apiClient = CreateApiClient(fakeRequestHandler, clock);

            var read = apiClient.HttpClient.GetAsync(ResourceRelativeUrl);

            await WaitUntilAsync(() => CountReads(fakeRequestHandler) == 1);

            // Just short of the two seconds the API asked for, the read has not been replayed
            clock.Advance(TimeSpan.FromMilliseconds(1_900));
            await Task.Delay(50);

            Assert.That(
                CountReads(fakeRequestHandler),
                Is.EqualTo(1),
                "The read was replayed before the API said it would start answering again.");

            // Past those two seconds it is
            clock.Advance(TimeSpan.FromMilliseconds(100));

            using var response = await read.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(CountReads(fakeRequestHandler), Is.EqualTo(2));

            // A rejected response left open would pin a connection for the length of the wait (see APIPUB-134)
            Assert.That(rejectedContent.ContentDisposed, Is.True);
        }

        [Test]
        public async Task A_read_rejected_with_a_retry_after_date_should_not_be_replayed_before_that_date()
        {
            var fakeRequestHandler = GivenASourceApi();

            var clock = new FakeTimeProvider();

            string retryAfterDate = clock.GetUtcNow().UtcDateTime.AddSeconds(90).ToString("R");

            A.CallTo(() => fakeRequestHandler.Get(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(() => TooManyRequests(retryAfterDate))
                .Once()
                .Then.ReturnsLazily(() => Ok());

            using var apiClient = CreateApiClient(fakeRequestHandler, clock);

            var read = apiClient.HttpClient.GetAsync(ResourceRelativeUrl);

            await WaitUntilAsync(() => CountReads(fakeRequestHandler) == 1);

            clock.Advance(TimeSpan.FromSeconds(89));
            await Task.Delay(50);

            Assert.That(
                CountReads(fakeRequestHandler),
                Is.EqualTo(1),
                "The read was replayed before the date the API said it would start answering again.");

            clock.Advance(TimeSpan.FromSeconds(1));

            using var response = await read.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(CountReads(fakeRequestHandler), Is.EqualTo(2));
        }

        [TestCase(null, TestName = "with no Retry-After header at all")]
        [TestCase("whenever we feel like it", TestName = "with a Retry-After the API cannot have meant")]
        public async Task A_read_rejected_without_a_usable_retry_after_should_fall_back_on_the_configured_back_off(
            string retryAfter)
        {
            var fakeRequestHandler = GivenASourceApi();

            A.CallTo(() => fakeRequestHandler.Get(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(() => TooManyRequests(retryAfter))
                .Once()
                .Then.ReturnsLazily(() => Ok());

            var clock = new FakeTimeProvider();

            using var apiClient = CreateApiClient(fakeRequestHandler, clock);

            var read = apiClient.HttpClient.GetAsync(ResourceRelativeUrl);

            await WaitUntilAsync(() => CountReads(fakeRequestHandler) == 1);

            // The first delay of the exponential back off, which is what the read is waiting out
            clock.Advance(RetryStartingDelay - TimeSpan.FromMilliseconds(50));
            await Task.Delay(50);

            Assert.That(
                CountReads(fakeRequestHandler),
                Is.EqualTo(1),
                "The read was replayed before the configured back off had elapsed.");

            clock.Advance(TimeSpan.FromMilliseconds(50));

            using var response = await read.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(CountReads(fakeRequestHandler), Is.EqualTo(2));
        }

        [Test]
        public async Task A_read_the_api_never_stops_rejecting_should_be_reported_to_the_caller_after_the_retries_run_out()
        {
            var fakeRequestHandler = GivenASourceApi();

            A.CallTo(() => fakeRequestHandler.Get(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(() => TooManyRequests("1"));

            var clock = new FakeTimeProvider();

            using var apiClient = CreateApiClient(fakeRequestHandler, clock);

            var read = apiClient.HttpClient.GetAsync(ResourceRelativeUrl);

            await clock.AdvanceUntilCompletedAsync(read, TimeSpan.FromSeconds(1));

            using var response = await read;

            // The rejection reaches the caller, which is the same failure path a read that kept failing with 503
            // takes today: the caller records it and overall processing is forced to fail
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));

            // The first attempt plus every retry it was allowed, and no more
            Assert.That(CountReads(fakeRequestHandler), Is.EqualTo(MaxRetryAttempts + 1));
        }

        /// <summary>
        /// The scope decision recorded in the APIPUB-136 spike: 429 handling is for reads. A write is left to its
        /// caller even on a client that is configured to wait out a rejected read.
        /// </summary>
        [Test]
        public async Task A_write_rejected_as_too_many_requests_should_be_left_to_the_caller()
        {
            var fakeRequestHandler = GivenASourceApi();

            A.CallTo(() => fakeRequestHandler.Post(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(() => TooManyRequests("1"));

            var clock = new FakeTimeProvider();

            using var apiClient = CreateApiClient(fakeRequestHandler, clock);

            using var response = await apiClient
                .HttpClient.PostAsync(ResourceRelativeUrl, new StringContent("{}"))
                .WaitAsync(TimeSpan.FromSeconds(30));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));

            A.CallTo(() => fakeRequestHandler.Post(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .MustHaveHappened(1, Times.Exactly);
        }

        private static IFakeHttpRequestHandler GivenASourceApi()
        {
            TestHelpers.InitializeLogging();

            var fakeRequestHandler = A.Fake<IFakeHttpRequestHandler>().SetBaseUrl(MockRequests.SourceApiBaseUrl);

            A.CallTo(() => fakeRequestHandler.Post(TokenUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(() => FakeResponse.OK(new { access_token = "test-access-token" }));

            return fakeRequestHandler;
        }

        private static EdFiApiClient CreateApiClient(
            IFakeHttpRequestHandler fakeRequestHandler,
            TimeProvider timeProvider)
        {
            return new EdFiApiClient(
                "TestSource",
                TestHelpers.GetSourceApiConnectionDetails(),
                bearerTokenRefreshMinutes: 60,
                ignoreSslErrors: true,
                new HttpClientHandlerFakeBridge(fakeRequestHandler),
                timeProvider,
                new ApiThrottlingPolicy
                {
                    MaxRetryAttempts = MaxRetryAttempts,
                    RetryStartingDelay = RetryStartingDelay,
                });
        }

        private static int CountReads(IFakeHttpRequestHandler fakeRequestHandler) =>
            Fake.GetCalls(fakeRequestHandler).Count(call => call.Method.Name == "Get");

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            var timeout = Task.Delay(TimeSpan.FromSeconds(10));

            while (!condition())
            {
                Assert.That(timeout.IsCompleted, Is.False, "The awaited condition was not met in time.");

                await Task.Delay(10);
            }
        }

        private static HttpResponseMessage TooManyRequests(string retryAfter, HttpContent content = null)
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = content ?? new InstrumentedJsonContent(@"{""message"":""too many requests""}")
            };

            if (retryAfter is not null)
            {
                response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            }

            return response;
        }

        private static HttpResponseMessage Ok() => FakeResponse.OK(new { });
    }
}
