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
using FakeItEasy;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using Serilog.Events;
using Serilog.Sinks.TestCorrelator;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// Covers the token lifecycle on its own: the interval it settles on, what it does with the decision the policy
    /// hands it, and when it stops trying altogether. The scheduling decision is exercised directly through a refresh
    /// attempt, and the timer that acts on it is exercised against a fake clock so that a real
    /// <see cref="ITimer" /> fires, is rearmed and stops without the test having to wait.
    /// </summary>
    [TestFixture]
    public class BearerTokenRefreshTests
    {
        private const string TokenUrl = MockRequests.SourceApiBaseUrl + "/oauth/token";
        private const string ResourceUrl = MockRequests.SourceApiBaseUrl + "/data/v3/ed-fi/schools";
        private const string ResourceRelativeUrl = "data/v3/ed-fi/schools";
        private const string AnyToken = "any-access-token";

        private static readonly TimeSpan ConfiguredInterval = TimeSpan.FromMinutes(28);

        [Test]
        public void The_refresh_interval_is_capped_at_half_of_the_reported_token_lifetime()
        {
            // A 30 minute token, which is what the Ed-Fi ODS / API issues, against the documented default interval
            var fakeRequestHandler = GivenTheTokenEndpoint(out _, tokenLifetimeSeconds: 1800);

            using var tokenManager = CreateTokenManager(fakeRequestHandler);

            // Asserted on the effective interval rather than on a rendered log message, which would depend on
            // wording and on the decimal separator of the current culture
            Assert.That(tokenManager.RefreshInterval, Is.EqualTo(TimeSpan.FromMinutes(15)));
        }

        [Test]
        public void The_configured_interval_is_kept_when_the_api_reports_no_token_lifetime()
        {
            var fakeRequestHandler = GivenTheTokenEndpoint(out _, tokenLifetimeSeconds: null);

            using var tokenManager = CreateTokenManager(fakeRequestHandler);

            Assert.That(tokenManager.RefreshInterval, Is.EqualTo(ConfiguredInterval));
        }

        [Test]
        public void A_token_lifetime_under_a_minute_is_honored_and_reported()
        {
            var fakeRequestHandler = GivenTheTokenEndpoint(out _, tokenLifetimeSeconds: 20);

            using (TestCorrelator.CreateContext())
            {
                using var tokenManager = CreateTokenManager(fakeRequestHandler);

                // Half the lifetime, however short, so that the refresh still comes before the expiry
                Assert.That(tokenManager.RefreshInterval, Is.EqualTo(TimeSpan.FromSeconds(10)));

                var warnings = TestCorrelator.GetLogEventsFromCurrentContext()
                    .Where(logEvent => logEvent.Level == LogEventLevel.Warning)
                    .Select(logEvent => logEvent.RenderMessage())
                    .ToList();

                Assert.That(
                    warnings,
                    Has.Exactly(1).Contains("refreshed every"),
                    $"Warnings: {string.Join(Environment.NewLine, warnings)}");
            }
        }

        [Test]
        public void A_successful_refresh_schedules_the_next_attempt_at_the_effective_interval()
        {
            var fakeRequestHandler = GivenTheTokenEndpoint(out _, tokenLifetimeSeconds: 1800);

            using var tokenManager = CreateTokenManager(fakeRequestHandler);

            Assert.That(tokenManager.TryRefreshBearerToken(), Is.EqualTo(tokenManager.RefreshInterval));
            Assert.That(tokenManager.IsAuthenticationFailed, Is.False);
        }

        [Test]
        public void A_failed_refresh_is_retried_on_a_backoff_while_the_current_token_is_still_valid()
        {
            var fakeRequestHandler = GivenTheTokenEndpoint(out var failTokenRequests, tokenLifetimeSeconds: 3600);

            using var tokenManager = CreateTokenManager(fakeRequestHandler);

            failTokenRequests.Value = true;

            // The token has most of an hour left, so the failures are retried instead of ending the run
            Assert.That(tokenManager.TryRefreshBearerToken(), Is.EqualTo(TimeSpan.FromSeconds(5)));
            Assert.That(tokenManager.TryRefreshBearerToken(), Is.EqualTo(TimeSpan.FromSeconds(10)));
            Assert.That(tokenManager.TryRefreshBearerToken(), Is.EqualTo(TimeSpan.FromSeconds(20)));
            Assert.That(tokenManager.IsAuthenticationFailed, Is.False);
        }

        [Test]
        public void A_refresh_that_keeps_failing_with_no_reported_lifetime_stops_the_run()
        {
            // Without expires_in there is no runway to measure, so the failure count is what decides
            var fakeRequestHandler = GivenTheTokenEndpoint(out var failTokenRequests, tokenLifetimeSeconds: null);

            using var tokenManager = CreateTokenManager(fakeRequestHandler);

            failTokenRequests.Value = true;

            for (int attempt = 1; attempt < BearerTokenRefreshPolicy.MaxConsecutiveFailuresWithoutUsableToken; attempt++)
            {
                Assert.That(
                    tokenManager.TryRefreshBearerToken(),
                    Is.Not.Null,
                    $"Attempt {attempt} should still have been retried.");

                Assert.That(tokenManager.IsAuthenticationFailed, Is.False);
            }

            // The attempt that exhausts what is tolerated stops rearming the timer and fails the manager
            Assert.That(tokenManager.TryRefreshBearerToken(), Is.Null);
            Assert.That(tokenManager.IsAuthenticationFailed, Is.True);
        }

        [Test]
        public void A_recovered_refresh_starts_the_failure_count_over()
        {
            var fakeRequestHandler = GivenTheTokenEndpoint(out var failTokenRequests, tokenLifetimeSeconds: 3600);

            using var tokenManager = CreateTokenManager(fakeRequestHandler);

            failTokenRequests.Value = true;
            tokenManager.TryRefreshBearerToken();
            tokenManager.TryRefreshBearerToken();

            failTokenRequests.Value = false;
            Assert.That(tokenManager.TryRefreshBearerToken(), Is.EqualTo(tokenManager.RefreshInterval));

            // Back to the first backoff step rather than continuing where the earlier failures left off
            failTokenRequests.Value = true;
            Assert.That(tokenManager.TryRefreshBearerToken(), Is.EqualTo(TimeSpan.FromSeconds(5)));
        }

        [Test]
        public void The_timer_refreshes_the_token_when_the_interval_elapses_and_rearms_itself()
        {
            var fakeRequestHandler = GivenTheTokenEndpoint(out _, tokenLifetimeSeconds: 1800);
            var clock = new FakeTimeProvider();

            using var tokenManager = CreateTokenManager(fakeRequestHandler, clock);

            var interval = TimeSpan.FromMinutes(15);
            Assert.That(tokenManager.RefreshInterval, Is.EqualTo(interval));
            Assert.That(CountTokenRequests(fakeRequestHandler), Is.EqualTo(1), "Only the initial acquisition so far.");

            clock.Advance(interval - TimeSpan.FromSeconds(1));
            Assert.That(CountTokenRequests(fakeRequestHandler), Is.EqualTo(1), "The timer must not fire early.");

            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.That(CountTokenRequests(fakeRequestHandler), Is.EqualTo(2), "The timer should have fired at the interval.");

            // The timer is one-shot and rearmed after each attempt, so a second interval has to produce a third request
            clock.Advance(interval);
            Assert.That(CountTokenRequests(fakeRequestHandler), Is.EqualTo(3), "The timer should have been rearmed.");
        }

        [Test]
        public void The_timer_retries_a_failed_refresh_on_the_backoff_and_returns_to_the_interval_once_it_recovers()
        {
            // An hour long token, so the configured interval is the shorter of the two
            var fakeRequestHandler = GivenTheTokenEndpoint(out var failTokenRequests, tokenLifetimeSeconds: 3600);
            var clock = new FakeTimeProvider();

            using var tokenManager = CreateTokenManager(fakeRequestHandler, clock);

            Assert.That(tokenManager.RefreshInterval, Is.EqualTo(ConfiguredInterval));

            clock.Advance(ConfiguredInterval);
            Assert.That(CountTokenRequests(fakeRequestHandler), Is.EqualTo(2), "The first scheduled refresh should have succeeded.");

            failTokenRequests.Value = true;

            clock.Advance(ConfiguredInterval);
            Assert.That(CountTokenRequests(fakeRequestHandler), Is.EqualTo(3), "The second scheduled refresh should have been attempted and failed.");

            // Retried 5 seconds later rather than a full interval later
            clock.Advance(TimeSpan.FromSeconds(4));
            Assert.That(CountTokenRequests(fakeRequestHandler), Is.EqualTo(3));
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.That(CountTokenRequests(fakeRequestHandler), Is.EqualTo(4), "The first retry should have fired after 5 seconds.");

            clock.Advance(TimeSpan.FromSeconds(10));
            Assert.That(CountTokenRequests(fakeRequestHandler), Is.EqualTo(5), "The second retry should have fired after 10 seconds.");

            failTokenRequests.Value = false;

            clock.Advance(TimeSpan.FromSeconds(20));
            Assert.That(CountTokenRequests(fakeRequestHandler), Is.EqualTo(6), "The third retry should have fired after 20 seconds.");
            Assert.That(tokenManager.IsAuthenticationFailed, Is.False, "The token was still valid throughout, so the run must not have ended.");

            // Recovered, so the next refresh is a full interval away again
            clock.Advance(ConfiguredInterval - TimeSpan.FromSeconds(1));
            Assert.That(CountTokenRequests(fakeRequestHandler), Is.EqualTo(6));
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.That(CountTokenRequests(fakeRequestHandler), Is.EqualTo(7), "The regular interval should have resumed after the recovery.");
        }

        [Test]
        public void Once_the_timer_path_gives_up_the_timer_is_not_rearmed()
        {
            var fakeRequestHandler = GivenTheTokenEndpoint(out var failTokenRequests, tokenLifetimeSeconds: null);
            var clock = new FakeTimeProvider();

            using var tokenManager = CreateTokenManager(fakeRequestHandler, clock);

            failTokenRequests.Value = true;

            // The scheduled refresh fails, and so does every retry on the 5, 10, 20 and 40 second backoff. Each retry is
            // armed from inside the previous attempt, so the clock is advanced one retry at a time.
            clock.Advance(ConfiguredInterval);

            foreach (int retryDelaySeconds in new[] { 5, 10, 20, 40 })
            {
                clock.Advance(TimeSpan.FromSeconds(retryDelaySeconds));
            }

            Assert.That(
                CountTokenRequests(fakeRequestHandler),
                Is.EqualTo(1 + BearerTokenRefreshPolicy.MaxConsecutiveFailuresWithoutUsableToken),
                "The initial acquisition plus every tolerated failure.");
            Assert.That(tokenManager.IsAuthenticationFailed, Is.True);

            // No further attempt is made, however long the run would otherwise go on
            clock.Advance(TimeSpan.FromHours(3));
            Assert.That(
                CountTokenRequests(fakeRequestHandler),
                Is.EqualTo(1 + BearerTokenRefreshPolicy.MaxConsecutiveFailuresWithoutUsableToken),
                "The timer must not have been rearmed after giving up.");
        }

        [Test]
        public async Task Once_authentication_has_failed_the_timer_stops_and_requests_short_circuit()
        {
            var fakeRequestHandler = GivenTheTokenEndpoint(out var failTokenRequests, tokenLifetimeSeconds: null);

            A.CallTo(() => fakeRequestHandler.Get(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(() => FakeResponse.OK(new { }));

            var transportHandler = new HttpClientHandlerFakeBridge(fakeRequestHandler);

            using var tokenManager = new BearerTokenManager(
                "TestSource",
                TestHelpers.GetSourceApiConnectionDetails(),
                bearerTokenRefreshMinutes: 28,
                transportHandler);

            using var httpClient = new HttpClient(
                new BearerTokenHandler(transportHandler, tokenManager, "TestSource"))
            {
                BaseAddress = new Uri(MockRequests.SourceApiBaseUrl + "/")
            };

            failTokenRequests.Value = true;

            for (int attempt = 0; attempt < BearerTokenRefreshPolicy.MaxConsecutiveFailuresWithoutUsableToken; attempt++)
            {
                tokenManager.TryRefreshBearerToken();
            }

            Assert.That(tokenManager.IsAuthenticationFailed, Is.True);

            int tokenRequestsSoFar = CountTokenRequests(fakeRequestHandler);

            Assert.That(tokenManager.TryRefreshBearerToken(), Is.Null, "The timer should not be rearmed.");
            Assert.That(
                CountTokenRequests(fakeRequestHandler),
                Is.EqualTo(tokenRequestsSoFar),
                "No further token request should have been attempted.");

            Exception caught = null;

            try
            {
                await httpClient.GetAsync(ResourceRelativeUrl);
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            Assert.That(
                EdFiApiAuthenticationException.IsRepresentedBy(caught),
                Is.True,
                $"Unexpected exception: {caught}");

            A.CallTo(() => fakeRequestHandler.Get(ResourceUrl, A<HttpRequestMessage>.Ignored)).MustNotHaveHappened();
        }

        [Test]
        public async Task Disposing_the_manager_while_a_refresh_is_in_flight_waits_for_it_and_completes_cleanly()
        {
            using var tokenRequestStarted = new ManualResetEventSlim();
            using var releaseTokenRequest = new ManualResetEventSlim();

            var fakeRequestHandler = A.Fake<IFakeHttpRequestHandler>().SetBaseUrl(MockRequests.SourceApiBaseUrl);

            int tokenRequests = 0;

            // The initial acquisition returns at once; the refresh under test is held until the test lets it go
            A.CallTo(() => fakeRequestHandler.Post(TokenUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(
                    () =>
                    {
                        if (Interlocked.Increment(ref tokenRequests) > 1)
                        {
                            tokenRequestStarted.Set();
                            releaseTokenRequest.Wait(TimeSpan.FromSeconds(10));
                        }

                        return TokenResponse(expiresInSeconds: null);
                    });

            TestHelpers.InitializeLogging();

            var tokenManager = CreateTokenManager(fakeRequestHandler);

            var refresh = Task.Run(() => tokenManager.TryRefreshBearerToken());

            Assert.That(tokenRequestStarted.Wait(TimeSpan.FromSeconds(10)), Is.True, "The refresh should have reached the token endpoint.");

            var disposal = Task.Run(() => tokenManager.Dispose());

            // The disposal has to wait for the refresh that holds the lock rather than dispose the lock out from
            // under it
            await Task.Delay(200);
            Assert.That(disposal.IsCompleted, Is.False, "Disposal should wait for the refresh in flight.");

            releaseTokenRequest.Set();

            await Task.WhenAll(refresh, disposal).WaitAsync(TimeSpan.FromSeconds(10));

            // Neither side threw: the refresh reported its outcome through its return value, and the disposal completed
            Assert.That(refresh.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            Assert.That(disposal.Status, Is.EqualTo(TaskStatus.RanToCompletion));
        }

        [Test]
        public void Disposing_the_manager_stops_the_timer()
        {
            var fakeRequestHandler = GivenTheTokenEndpoint(out _, tokenLifetimeSeconds: 1800);
            var clock = new FakeTimeProvider();

            var tokenManager = CreateTokenManager(fakeRequestHandler, clock);
            tokenManager.Dispose();

            clock.Advance(TimeSpan.FromHours(1));

            Assert.That(CountTokenRequests(fakeRequestHandler), Is.EqualTo(1), "A disposed manager must not refresh its token.");
        }

        [Test]
        public void The_retry_exclusion_predicate_recognizes_a_wrapped_authentication_failure()
        {
            // The five retry policies across the three block factories all reference this predicate, so covering it
            // here covers the exclusion at every one of them.
            var wrapped = new HttpRequestException(
                "as wrapped by HttpClient",
                new EdFiApiAuthenticationException("the token is gone"));

            Assert.That(EdFiApiAuthenticationException.IsNotRepresentedBy(wrapped), Is.False);
            Assert.That(
                EdFiApiAuthenticationException.IsNotRepresentedBy(new HttpRequestException("connection reset")),
                Is.True);
            Assert.That(EdFiApiAuthenticationException.IsNotRepresentedBy(null), Is.True);
        }

        private static IFakeHttpRequestHandler GivenTheTokenEndpoint(
            out MutableFlag failTokenRequests,
            int? tokenLifetimeSeconds)
        {
            var flag = new MutableFlag();
            failTokenRequests = flag;

            var fakeRequestHandler = A.Fake<IFakeHttpRequestHandler>().SetBaseUrl(MockRequests.SourceApiBaseUrl);

            A.CallTo(() => fakeRequestHandler.Post(TokenUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(
                    () =>
                        flag.Value
                            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                            {
                                Content = new StringContent("{}", Encoding.UTF8, "application/json")
                            }
                            : TokenResponse(tokenLifetimeSeconds));

            TestHelpers.InitializeLogging();

            return fakeRequestHandler;
        }

        private static int CountTokenRequests(IFakeHttpRequestHandler fakeRequestHandler) =>
            Fake.GetCalls(fakeRequestHandler).Count(call => call.Method.Name == "Post");

        private static HttpResponseMessage TokenResponse(int? expiresInSeconds) =>
            expiresInSeconds == null
                ? FakeResponse.OK(new { access_token = AnyToken })
                : FakeResponse.OK(new { access_token = AnyToken, expires_in = expiresInSeconds.Value });

        private static BearerTokenManager CreateTokenManager(
            IFakeHttpRequestHandler fakeRequestHandler,
            TimeProvider timeProvider = null) =>
            new BearerTokenManager(
                "TestSource",
                TestHelpers.GetSourceApiConnectionDetails(),
                bearerTokenRefreshMinutes: (int)ConfiguredInterval.TotalMinutes,
                new HttpClientHandlerFakeBridge(fakeRequestHandler),
                timeProvider);

        private sealed class MutableFlag
        {
            public bool Value { get; set; }
        }
    }
}
