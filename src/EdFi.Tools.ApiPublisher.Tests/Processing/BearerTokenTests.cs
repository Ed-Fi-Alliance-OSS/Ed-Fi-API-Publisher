// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using NUnit.Framework;
using Serilog.Sinks.TestCorrelator;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    [TestFixture]
    public class BearerTokenTests
    {
        private const string TokenUrl = MockRequests.SourceApiBaseUrl + "/oauth/token";
        private const string ResourceUrl = MockRequests.SourceApiBaseUrl + "/data/v3/ed-fi/schools";
        private const string ResourceRelativeUrl = "data/v3/ed-fi/schools";
        private const string FirstToken = "first-access-token";
        private const string SecondToken = "second-access-token";

        [Test]
        public void When_the_initial_bearer_token_cannot_be_obtained_the_client_cannot_be_created()
        {
            var fakeRequestHandler = A.Fake<IFakeHttpRequestHandler>().SetBaseUrl(MockRequests.SourceApiBaseUrl);

            A.CallTo(() => fakeRequestHandler.Post(TokenUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(() => Unauthorized());

            TestHelpers.InitializeLogging();

            Exception caught = null;

            try
            {
                CreateApiClient(fakeRequestHandler).Dispose();
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            Assert.That(caught, Is.TypeOf<EdFiApiAuthenticationException>(), $"Unexpected exception: {caught}");
            Assert.That(caught.Message, Does.Contain("Unable to obtain initial bearer token"));
        }

        [Test]
        public async Task When_a_request_is_rejected_as_unauthorized_the_token_is_reacquired_and_the_request_is_replayed()
        {
            var fakeRequestHandler = A.Fake<IFakeHttpRequestHandler>().SetBaseUrl(MockRequests.SourceApiBaseUrl);

            GivenTheTokenEndpointReturns(fakeRequestHandler, FirstToken, SecondToken);

            var authorizationHeaders = new List<string>();

            A.CallTo(() => fakeRequestHandler.Get(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(
                    (string url, HttpRequestMessage request) =>
                    {
                        authorizationHeaders.Add(request.Headers.Authorization?.Parameter);

                        return authorizationHeaders.Count == 1 ? Unauthorized() : Ok();
                    });

            TestHelpers.InitializeLogging();

            using var apiClient = CreateApiClient(fakeRequestHandler);

            var response = await apiClient.HttpClient.GetAsync(ResourceRelativeUrl);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            // The request was sent once with the rejected token and replayed once with the re-acquired token
            Assert.That(authorizationHeaders, Is.EqualTo(new[] { FirstToken, SecondToken }));

            // One token request for the initial acquisition, one for the re-acquisition
            A.CallTo(() => fakeRequestHandler.Post(TokenUrl, A<HttpRequestMessage>.Ignored))
                .MustHaveHappened(2, Times.Exactly);

            Assert.That(apiClient.CurrentBearerToken, Is.EqualTo(SecondToken));
            Assert.That(apiClient.IsAuthenticationFailed, Is.False);
        }

        [Test]
        public async Task When_a_request_with_a_body_is_replayed_the_body_and_its_content_type_are_preserved()
        {
            const string RequestBody = "{\"schoolId\":255901001,\"nameOfInstitution\":\"Grand Bend High School\"}";

            var fakeRequestHandler = A.Fake<IFakeHttpRequestHandler>().SetBaseUrl(MockRequests.SourceApiBaseUrl);

            GivenTheTokenEndpointReturns(fakeRequestHandler, FirstToken, SecondToken);

            var postedBodies = new List<string>();
            var postedContentTypes = new List<string>();

            A.CallTo(() => fakeRequestHandler.Post(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(
                    (string url, HttpRequestMessage request) =>
                    {
                        postedBodies.Add(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                        postedContentTypes.Add(request.Content.Headers.ContentType?.ToString());

                        return postedBodies.Count == 1 ? Unauthorized() : Ok();
                    });

            TestHelpers.InitializeLogging();

            using var apiClient = CreateApiClient(fakeRequestHandler);

            var response = await apiClient.HttpClient.PostAsync(
                ResourceRelativeUrl,
                new StringContent(RequestBody, Encoding.UTF8, "application/json"));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(postedBodies, Is.EqualTo(new[] { RequestBody, RequestBody }));
            Assert.That(postedContentTypes.Distinct().Count(), Is.EqualTo(1), "Content type was not preserved on the replayed request.");
            Assert.That(postedContentTypes[1], Does.Contain("application/json"));
        }

        [Test]
        public async Task When_the_token_cannot_be_reacquired_the_unauthorized_response_is_returned_to_the_caller()
        {
            var fakeRequestHandler = A.Fake<IFakeHttpRequestHandler>().SetBaseUrl(MockRequests.SourceApiBaseUrl);

            // The initial acquisition succeeds; every later token request fails
            A.CallTo(() => fakeRequestHandler.Post(TokenUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(() => TokenResponse(FirstToken))
                .Once()
                .Then.ReturnsLazily(() => Unauthorized());

            A.CallTo(() => fakeRequestHandler.Get(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(() => Unauthorized());

            TestHelpers.InitializeLogging();

            using var apiClient = CreateApiClient(fakeRequestHandler);

            var response = await apiClient.HttpClient.GetAsync(ResourceRelativeUrl);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

            // The request is not replayed when there is no usable token to replay it with
            A.CallTo(() => fakeRequestHandler.Get(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .MustHaveHappened(1, Times.Exactly);
        }

        [Test]
        public async Task When_concurrent_requests_are_rejected_as_unauthorized_the_token_is_reacquired_once()
        {
            const int ConcurrentRequests = 8;

            var fakeRequestHandler = A.Fake<IFakeHttpRequestHandler>().SetBaseUrl(MockRequests.SourceApiBaseUrl);

            GivenTheTokenEndpointReturns(fakeRequestHandler, FirstToken, SecondToken);

            // The API rejects the expired token and accepts the re-acquired one, which is what happens to the
            // requests that are already in flight when a token expires
            A.CallTo(() => fakeRequestHandler.Get(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(
                    (string url, HttpRequestMessage request) =>
                        request.Headers.Authorization?.Parameter == FirstToken ? Unauthorized() : Ok());

            TestHelpers.InitializeLogging();

            using var apiClient = CreateApiClient(fakeRequestHandler);

            var responses = await Task.WhenAll(
                Enumerable
                    .Range(0, ConcurrentRequests)
                    .Select(_ => apiClient.HttpClient.GetAsync(ResourceRelativeUrl)));

            Assert.That(
                responses.Select(response => response.StatusCode),
                Is.All.EqualTo(HttpStatusCode.OK));

            // Every rejected request holds the same stale token, so one re-acquisition serves all of them
            A.CallTo(() => fakeRequestHandler.Post(TokenUrl, A<HttpRequestMessage>.Ignored))
                .MustHaveHappened(2, Times.Exactly);
        }

        [Test]
        public async Task When_authentication_has_failed_requests_are_not_attempted()
        {
            var fakeRequestHandler = A.Fake<IFakeHttpRequestHandler>().SetBaseUrl(MockRequests.SourceApiBaseUrl);

            A.CallTo(() => fakeRequestHandler.Get(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(() => Ok());

            var bearerTokenProvider = A.Fake<IBearerTokenProvider>();
            A.CallTo(() => bearerTokenProvider.IsAuthenticationFailed).Returns(true);

            TestHelpers.InitializeLogging();

            using var httpClient = new HttpClient(
                new BearerTokenHandler(
                    new HttpClientHandlerFakeBridge(fakeRequestHandler),
                    bearerTokenProvider,
                    "TestSource"))
            {
                BaseAddress = new System.Uri(MockRequests.SourceApiBaseUrl + "/")
            };

            Exception caught = null;

            try
            {
                await httpClient.GetAsync(ResourceRelativeUrl);
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            Assert.That(caught, Is.Not.Null, "The request should not have been sent without a usable token.");
            Assert.That(
                EdFiApiAuthenticationException.IsRepresentedBy(caught),
                Is.True,
                $"Unexpected exception: {caught}");

            A.CallTo(() => fakeRequestHandler.Get(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .MustNotHaveHappened();
        }

        [Test]
        public void When_the_api_reports_a_token_lifetime_the_refresh_interval_is_derived_from_it()
        {
            var fakeRequestHandler = A.Fake<IFakeHttpRequestHandler>().SetBaseUrl(MockRequests.SourceApiBaseUrl);

            // A 30 minute token, which is what the Ed-Fi ODS / API issues
            A.CallTo(() => fakeRequestHandler.Post(TokenUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(() => TokenResponse(FirstToken, expiresInSeconds: 1800));

            TestHelpers.InitializeLogging();

            using (TestCorrelator.CreateContext())
            {
                using var apiClient = CreateApiClient(fakeRequestHandler, bearerTokenRefreshMinutes: 28);

                var messages = TestCorrelator
                    .GetLogEventsFromCurrentContext()
                    .Select(logEvent => logEvent.RenderMessage())
                    .ToList();

                Assert.That(
                    messages.Any(message => message.Contains("refresh interval") && message.Contains("15.0 minutes")),
                    Is.True,
                    $"Expected the refresh interval to be halved to 15 minutes. Messages: {string.Join(" | ", messages)}");
            }
        }

        private static EdFiApiClient CreateApiClient(
            IFakeHttpRequestHandler fakeRequestHandler,
            int bearerTokenRefreshMinutes = 60)
        {
            return new EdFiApiClient(
                "TestSource",
                TestHelpers.GetSourceApiConnectionDetails(),
                bearerTokenRefreshMinutes,
                ignoreSslErrors: true,
                new HttpClientHandlerFakeBridge(fakeRequestHandler));
        }

        private static void GivenTheTokenEndpointReturns(
            IFakeHttpRequestHandler fakeRequestHandler,
            string firstToken,
            string secondToken)
        {
            A.CallTo(() => fakeRequestHandler.Post(TokenUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(() => TokenResponse(firstToken))
                .Once()
                .Then.ReturnsLazily(() => TokenResponse(secondToken));
        }

        private static HttpResponseMessage TokenResponse(string accessToken, int? expiresInSeconds = null)
        {
            return expiresInSeconds == null
                ? FakeResponse.OK(new { access_token = accessToken })
                : FakeResponse.OK(new { access_token = accessToken, expires_in = expiresInSeconds.Value });
        }

        private static HttpResponseMessage Ok() => FakeResponse.OK(new { });

        private static HttpResponseMessage Unauthorized() =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":\"invalid_token\"}", Encoding.UTF8, "application/json")
            };
    }
}
