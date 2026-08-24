// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using NUnit.Framework;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// Covers what the periodic refresh does with the decision the policy hands it: how the next attempt is
    /// scheduled, and when the client stops trying altogether. Driven through the refresh attempt itself rather than
    /// by waiting for a real timer to fire.
    /// </summary>
    [TestFixture]
    public class BearerTokenRefreshTests
    {
        private const string TokenUrl = MockRequests.SourceApiBaseUrl + "/oauth/token";
        private const string ResourceUrl = MockRequests.SourceApiBaseUrl + "/data/v3/ed-fi/schools";
        private const string ResourceRelativeUrl = "data/v3/ed-fi/schools";
        private const string AnyToken = "any-access-token";

        [Test]
        public void A_successful_refresh_schedules_the_next_attempt_at_the_effective_interval()
        {
            var fakeRequestHandler = GivenTheTokenEndpoint(out _, tokenLifetimeSeconds: 1800);

            using var apiClient = CreateApiClient(fakeRequestHandler);

            Assert.That(apiClient.TryRefreshBearerToken(), Is.EqualTo(apiClient.RefreshInterval));
            Assert.That(apiClient.IsAuthenticationFailed, Is.False);
        }

        [Test]
        public void A_failed_refresh_is_retried_on_a_backoff_while_the_current_token_is_still_valid()
        {
            var fakeRequestHandler = GivenTheTokenEndpoint(out var failTokenRequests, tokenLifetimeSeconds: 3600);

            using var apiClient = CreateApiClient(fakeRequestHandler);

            failTokenRequests.Value = true;

            // The token has most of an hour left, so the failures are retried instead of ending the run
            Assert.That(apiClient.TryRefreshBearerToken(), Is.EqualTo(TimeSpan.FromSeconds(5)));
            Assert.That(apiClient.TryRefreshBearerToken(), Is.EqualTo(TimeSpan.FromSeconds(10)));
            Assert.That(apiClient.TryRefreshBearerToken(), Is.EqualTo(TimeSpan.FromSeconds(20)));
            Assert.That(apiClient.IsAuthenticationFailed, Is.False);
        }

        [Test]
        public void A_refresh_that_keeps_failing_with_no_reported_lifetime_stops_the_run()
        {
            // Without expires_in there is no runway to measure, so the failure count is what decides
            var fakeRequestHandler = GivenTheTokenEndpoint(out var failTokenRequests, tokenLifetimeSeconds: null);

            using var apiClient = CreateApiClient(fakeRequestHandler);

            failTokenRequests.Value = true;

            for (int attempt = 1; attempt < BearerTokenRefreshPolicy.MaxConsecutiveFailuresWithUnknownLifetime; attempt++)
            {
                Assert.That(
                    apiClient.TryRefreshBearerToken(),
                    Is.Not.Null,
                    $"Attempt {attempt} should still have been retried.");

                Assert.That(apiClient.IsAuthenticationFailed, Is.False);
            }

            // The attempt that exhausts what is tolerated stops rearming the timer and fails the client
            Assert.That(apiClient.TryRefreshBearerToken(), Is.Null);
            Assert.That(apiClient.IsAuthenticationFailed, Is.True);
        }

        [Test]
        public void A_recovered_refresh_starts_the_failure_count_over()
        {
            var fakeRequestHandler = GivenTheTokenEndpoint(out var failTokenRequests, tokenLifetimeSeconds: 3600);

            using var apiClient = CreateApiClient(fakeRequestHandler);

            failTokenRequests.Value = true;
            apiClient.TryRefreshBearerToken();
            apiClient.TryRefreshBearerToken();

            failTokenRequests.Value = false;
            Assert.That(apiClient.TryRefreshBearerToken(), Is.EqualTo(apiClient.RefreshInterval));

            // Back to the first backoff step rather than continuing where the earlier failures left off
            failTokenRequests.Value = true;
            Assert.That(apiClient.TryRefreshBearerToken(), Is.EqualTo(TimeSpan.FromSeconds(5)));
        }

        [Test]
        public async Task Once_authentication_has_failed_the_timer_stops_and_requests_short_circuit()
        {
            var fakeRequestHandler = GivenTheTokenEndpoint(out var failTokenRequests, tokenLifetimeSeconds: null);

            A.CallTo(() => fakeRequestHandler.Get(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(() => FakeResponse.OK(new { }));

            using var apiClient = CreateApiClient(fakeRequestHandler);

            failTokenRequests.Value = true;

            for (int attempt = 0; attempt < BearerTokenRefreshPolicy.MaxConsecutiveFailuresWithUnknownLifetime; attempt++)
            {
                apiClient.TryRefreshBearerToken();
            }

            Assert.That(apiClient.IsAuthenticationFailed, Is.True);

            int tokenRequestsSoFar = CountTokenRequests(fakeRequestHandler);

            Assert.That(apiClient.TryRefreshBearerToken(), Is.Null, "The timer should not be rearmed.");
            Assert.That(
                CountTokenRequests(fakeRequestHandler),
                Is.EqualTo(tokenRequestsSoFar),
                "No further token request should have been attempted.");

            Exception caught = null;

            try
            {
                await apiClient.HttpClient.GetAsync(ResourceRelativeUrl);
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

        private static EdFiApiClient CreateApiClient(IFakeHttpRequestHandler fakeRequestHandler) =>
            new EdFiApiClient(
                "TestSource",
                TestHelpers.GetSourceApiConnectionDetails(),
                bearerTokenRefreshMinutes: 28,
                ignoreSslErrors: true,
                new HttpClientHandlerFakeBridge(fakeRequestHandler));

        private sealed class MutableFlag
        {
            public bool Value { get; set; }
        }
    }
}
