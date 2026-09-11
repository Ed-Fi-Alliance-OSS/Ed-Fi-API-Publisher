// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
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
    /// Covers what the client does with a source read the API rejects as too many requests: how long it waits
    /// before replaying it, what it falls back on when the API does not say, what happens when the wait will not
    /// fit the time the request has, and what the caller ends up seeing when the API never stops rejecting it.
    /// </summary>
    /// <remarks>
    /// Each wait is measured rather than bracketed: the fake clock is stepped forward until the read completes and
    /// the assertions are made on the clock time between one attempt and the next. Advancing to a chosen instant
    /// instead would race the handler's registration of its timer, and a test that asserts an attempt has NOT
    /// happened yet can pass simply because it looked too early.
    /// </remarks>
    [TestFixture]
    public class SourceRetryAfterTests
    {
        private const string TokenUrl = MockRequests.SourceApiBaseUrl + "/oauth/token";
        private const string ResourceUrl = MockRequests.SourceApiBaseUrl + "/data/v3/ed-fi/schools";
        private const string ResourceRelativeUrl = "data/v3/ed-fi/schools";
        private const int MaxRetryAttempts = 2;

        private static readonly TimeSpan RetryStartingDelay = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// How far the fake clock moves per step while a read is in flight. Every measured wait is therefore known
        /// to within one step, which the assertions allow for.
        /// </summary>
        private static readonly TimeSpan ClockStep = TimeSpan.FromMilliseconds(50);

        [TestCase(2)]
        [TestCase(7)]
        public async Task A_read_rejected_with_a_retry_after_in_seconds_should_wait_for_what_the_api_asked(
            int retryAfterSeconds)
        {
            var clock = new FakeTimeProvider();
            var attempts = new List<DateTimeOffset>();

            var fakeRequestHandler = GivenASourceApi();

            var rejectedContent = new InstrumentedJsonContent(@"{""message"":""too many requests""}");

            GivenTheReadIsRejected(
                fakeRequestHandler,
                clock,
                attempts,
                () => TooManyRequests(retryAfterSeconds.ToString(), rejectedContent),
                rejectionCount: 1);

            using var apiClient = CreateApiClient(fakeRequestHandler, clock);

            using var response = await ReadAsync(apiClient, clock);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(attempts, Has.Count.EqualTo(2));

            // The wait tracks the header rather than being any fixed delay, which is what the two cases pin
            AssertWaitedAtLeast(attempts, TimeSpan.FromSeconds(retryAfterSeconds));

            // A rejected response left open would pin a connection for the length of the wait (see APIPUB-134)
            Assert.That(rejectedContent.ContentDisposed, Is.True);
        }

        [Test]
        public async Task A_read_rejected_with_a_retry_after_date_should_wait_until_that_date()
        {
            var clock = new FakeTimeProvider();
            var attempts = new List<DateTimeOffset>();

            var fakeRequestHandler = GivenASourceApi();

            var answeringAgainAt = clock.GetUtcNow().AddSeconds(30);

            GivenTheReadIsRejected(
                fakeRequestHandler,
                clock,
                attempts,
                () => TooManyRequests(answeringAgainAt.UtcDateTime.ToString("R")),
                rejectionCount: 1);

            using var apiClient = CreateApiClient(fakeRequestHandler, clock);

            using var response = await ReadAsync(apiClient, clock);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(attempts, Has.Count.EqualTo(2));

            // Waited until the instant the API named, not for a fixed span from the rejection
            Assert.That(attempts[1], Is.GreaterThanOrEqualTo(answeringAgainAt));
            Assert.That(attempts[1] - answeringAgainAt, Is.LessThan(ClockStep * 2));
        }

        [TestCase(null, TestName = "with no Retry-After header at all")]
        [TestCase("whenever we feel like it", TestName = "with a Retry-After the API cannot have meant")]
        public async Task A_read_rejected_without_a_usable_retry_after_should_fall_back_on_the_configured_back_off(
            string retryAfter)
        {
            var clock = new FakeTimeProvider();
            var attempts = new List<DateTimeOffset>();

            var fakeRequestHandler = GivenASourceApi();

            GivenTheReadIsRejected(fakeRequestHandler, clock, attempts, () => TooManyRequests(retryAfter), 1);

            using var apiClient = CreateApiClient(fakeRequestHandler, clock);

            using var response = await ReadAsync(apiClient, clock);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            AssertWaitedAtLeast(attempts, RetryStartingDelay);
        }

        [Test]
        public async Task The_back_off_should_double_with_each_attempt_the_api_keeps_rejecting()
        {
            var clock = new FakeTimeProvider();
            var attempts = new List<DateTimeOffset>();

            var fakeRequestHandler = GivenASourceApi();

            GivenTheReadIsRejected(fakeRequestHandler, clock, attempts, () => TooManyRequests(null), 2);

            using var apiClient = CreateApiClient(fakeRequestHandler, clock);

            using var response = await ReadAsync(apiClient, clock);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(attempts, Has.Count.EqualTo(3));

            var firstWait = attempts[1] - attempts[0];
            var secondWait = attempts[2] - attempts[1];

            Assert.That(firstWait, Is.GreaterThanOrEqualTo(RetryStartingDelay));
            Assert.That(secondWait, Is.GreaterThanOrEqualTo(RetryStartingDelay * 2));

            // A constant delay would keep the two the same, so the doubling is what this pins
            Assert.That(secondWait, Is.GreaterThan(firstWait));
        }

        /// <summary>
        /// The header is a floor on the wait rather than a replacement for the back off, so a source asking for
        /// less than the configured back off does not get retried any sooner.
        /// </summary>
        [Test]
        public async Task A_retry_after_shorter_than_the_back_off_should_not_shorten_the_wait()
        {
            var clock = new FakeTimeProvider();
            var attempts = new List<DateTimeOffset>();

            var backOff = TimeSpan.FromSeconds(5);

            var fakeRequestHandler = GivenASourceApi();

            GivenTheReadIsRejected(fakeRequestHandler, clock, attempts, () => TooManyRequests("1"), 1);

            using var apiClient = CreateApiClient(fakeRequestHandler, clock, retryStartingDelay: backOff);

            using var response = await ReadAsync(apiClient, clock);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            AssertWaitedAtLeast(attempts, backOff);
        }

        /// <summary>
        /// The wait is spent inside the caller's request, so it is bounded by the time that request has. A wait the
        /// remaining budget cannot cover is not started at all: waiting anyway would let the budget expire mid-wait
        /// and reach the caller as a cancelled request with no status instead of as the rejection it is.
        /// </summary>
        [Test]
        public async Task A_wait_that_will_not_fit_the_request_budget_should_report_the_rejection_rather_than_be_cut_short()
        {
            var clock = new FakeTimeProvider();
            var attempts = new List<DateTimeOffset>();

            var fakeRequestHandler = GivenASourceApi();

            // Well beyond the budget below, which is the shape of a real gateway asking for a minutes-long wait
            GivenTheReadIsRejected(fakeRequestHandler, clock, attempts, () => TooManyRequests("300"), 1);

            using var apiClient = CreateApiClient(
                fakeRequestHandler,
                clock,
                requestBudget: TimeSpan.FromSeconds(10));

            // No clock stepping: if the handler starts the wait at all, this never completes and the test fails
            using var response = await apiClient
                .HttpClient.GetAsync(ResourceRelativeUrl)
                .WaitAsync(TimeSpan.FromSeconds(10));

            // The caller sees the rejection, with its status, rather than a cancellation
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(attempts, Has.Count.EqualTo(1), "The read should not have been replayed.");
        }

        [Test]
        public async Task A_read_the_api_never_stops_rejecting_should_be_reported_to_the_caller_after_the_retries_run_out()
        {
            var clock = new FakeTimeProvider();
            var attempts = new List<DateTimeOffset>();

            var fakeRequestHandler = GivenASourceApi();

            GivenTheReadIsRejected(fakeRequestHandler, clock, attempts, () => TooManyRequests("1"), int.MaxValue);

            using var apiClient = CreateApiClient(fakeRequestHandler, clock);

            using var response = await ReadAsync(apiClient, clock);

            // The rejection reaches the caller, which is the same failure path a read that kept failing with 503
            // takes today: the caller records it and overall processing is forced to fail
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));

            // The first attempt plus every retry it was allowed, and no more
            Assert.That(attempts, Has.Count.EqualTo(MaxRetryAttempts + 1));
        }

        /// <summary>
        /// The scope decision recorded in the APIPUB-136 spike: 429 handling is for reads. A write is left to its
        /// caller even on a client that is configured to wait out a rejected read.
        /// </summary>
        [Test]
        public async Task A_write_rejected_as_too_many_requests_should_be_left_to_the_caller()
        {
            var clock = new FakeTimeProvider();

            var fakeRequestHandler = GivenASourceApi();

            A.CallTo(() => fakeRequestHandler.Post(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(() => TooManyRequests("1"));

            using var apiClient = CreateApiClient(fakeRequestHandler, clock);

            using var response = await apiClient
                .HttpClient.PostAsync(ResourceRelativeUrl, new StringContent("{}"))
                .WaitAsync(TimeSpan.FromSeconds(30));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));

            A.CallTo(() => fakeRequestHandler.Post(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .MustHaveHappened(1, Times.Exactly);
        }

        /// <summary>
        /// The handler keeps its waits inside the time the request has, so the budget it is given and the timeout
        /// the client enforces have to be the same value.
        /// </summary>
        [Test]
        public void The_request_budget_should_be_what_the_client_enforces_as_its_timeout()
        {
            var budget = TimeSpan.FromSeconds(42);

            var fakeRequestHandler = GivenASourceApi();

            using var apiClient = CreateApiClient(
                fakeRequestHandler,
                new FakeTimeProvider(),
                requestBudget: budget);

            Assert.That(apiClient.HttpClient.Timeout, Is.EqualTo(budget));
        }

        /// <summary>
        /// Steps the fake clock until the read completes, so the handler's timer is always registered before the
        /// clock passes it, and the wait can be read off the recorded attempt times.
        /// </summary>
        private static async Task<HttpResponseMessage> ReadAsync(EdFiApiClient apiClient, FakeTimeProvider clock)
        {
            var read = apiClient.HttpClient.GetAsync(ResourceRelativeUrl);

            await clock.AdvanceUntilCompletedAsync(read, ClockStep);

            return await read;
        }

        private static void AssertWaitedAtLeast(IReadOnlyList<DateTimeOffset> attempts, TimeSpan expected)
        {
            Assert.That(attempts, Has.Count.GreaterThanOrEqualTo(2));

            var waited = attempts[1] - attempts[0];

            Assert.That(waited, Is.GreaterThanOrEqualTo(expected));

            // The clock only moves in steps, so the wait is known to within one of them; a wait meaningfully
            // longer than asked for is as much a defect as one that is too short
            Assert.That(waited, Is.LessThan(expected + ClockStep * 2));
        }

        /// <summary>
        /// Records the fake time of every read and rejects the first <paramref name="rejectionCount" /> of them.
        /// </summary>
        private static void GivenTheReadIsRejected(
            IFakeHttpRequestHandler fakeRequestHandler,
            FakeTimeProvider clock,
            List<DateTimeOffset> attempts,
            Func<HttpResponseMessage> rejection,
            int rejectionCount)
        {
            A.CallTo(() => fakeRequestHandler.Get(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(
                    () =>
                    {
                        attempts.Add(clock.GetUtcNow());

                        return attempts.Count <= rejectionCount ? rejection() : Ok();
                    });
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
            TimeProvider timeProvider,
            TimeSpan? retryStartingDelay = null,
            TimeSpan? requestBudget = null)
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
                    TooManyRequestsRetryAttempts = MaxRetryAttempts,
                    TooManyRequestsRetryStartingDelay = retryStartingDelay ?? RetryStartingDelay,
                    RequestBudget = requestBudget ?? ApiThrottlingPolicy.DefaultRequestBudget,
                });
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
