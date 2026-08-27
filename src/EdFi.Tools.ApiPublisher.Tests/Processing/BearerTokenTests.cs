// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
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

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// Covers the request path: how a request carrying a rejected token is recovered through the API client's own
    /// pipeline, and what the caller sees when it cannot be.
    /// </summary>
    [TestFixture]
    public class BearerTokenTests
    {
        private const string TokenUrl = MockRequests.SourceApiBaseUrl + "/oauth/token";
        private const string ResourceUrl = MockRequests.SourceApiBaseUrl + "/data/v3/ed-fi/schools";
        private const string ResourceRelativeUrl = "data/v3/ed-fi/schools";
        private const string FirstToken = "first-access-token";
        private const string SecondToken = "second-access-token";
        private const string RequestBody = "{\"schoolId\":255901001,\"nameOfInstitution\":\"Grand Bend High School\"}";

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
        }

        [Test]
        public async Task When_a_request_with_a_body_is_replayed_the_body_and_its_content_type_are_preserved()
        {
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
        public async Task A_request_that_is_not_rejected_does_not_have_its_body_read_for_a_replay()
        {
            var fakeRequestHandler = A.Fake<IFakeHttpRequestHandler>().SetBaseUrl(MockRequests.SourceApiBaseUrl);

            GivenTheTokenEndpointReturns(fakeRequestHandler, FirstToken, SecondToken);

            // The fake transport does not read the body, so any serialization of it is the handler's doing
            A.CallTo(() => fakeRequestHandler.Post(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(() => Ok());

            TestHelpers.InitializeLogging();

            using var apiClient = CreateApiClient(fakeRequestHandler);

            var content = new SerializationCountingContent(RequestBody);

            var response = await apiClient.HttpClient.PostAsync(ResourceRelativeUrl, content);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(content.SerializationCount, Is.EqualTo(0), "The body must only be buffered when a replay needs it.");
        }

        [Test]
        public async Task A_rejected_request_whose_body_cannot_be_read_again_is_not_replayed()
        {
            var fakeRequestHandler = A.Fake<IFakeHttpRequestHandler>().SetBaseUrl(MockRequests.SourceApiBaseUrl);

            GivenTheTokenEndpointReturns(fakeRequestHandler, FirstToken, SecondToken);

            // Like a real transport, the fake consumes the body as it sends it
            A.CallTo(() => fakeRequestHandler.Post(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(
                    (string url, HttpRequestMessage request) =>
                    {
                        request.Content.CopyToAsync(Stream.Null).GetAwaiter().GetResult();

                        return Unauthorized();
                    });

            TestHelpers.InitializeLogging();

            using var apiClient = CreateApiClient(fakeRequestHandler);

            // A body streamed from a source that cannot be rewound cannot be sent a second time
            var content = new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes(RequestBody)));

            var response = await apiClient.HttpClient.PostAsync(ResourceRelativeUrl, content);

            // The unauthorized response is reported rather than a replay with a truncated body
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

            A.CallTo(() => fakeRequestHandler.Post(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .MustHaveHappened(1, Times.Exactly);
        }

        [Test]
        public async Task A_rejected_request_with_a_body_too_large_to_buffer_is_not_replayed()
        {
            var fakeRequestHandler = A.Fake<IFakeHttpRequestHandler>().SetBaseUrl(MockRequests.SourceApiBaseUrl);

            GivenTheTokenEndpointReturns(fakeRequestHandler, FirstToken, SecondToken);

            A.CallTo(() => fakeRequestHandler.Post(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(() => Unauthorized());

            TestHelpers.InitializeLogging();

            using var apiClient = CreateApiClient(fakeRequestHandler);

            // Declares a body one byte over the limit; the body itself stays small so the test does not allocate it
            var content = new SerializationCountingContent(
                RequestBody,
                declaredLength: BearerTokenHandler.MaxReplayableBodyLength + 1);

            var response = await apiClient.HttpClient.PostAsync(ResourceRelativeUrl, content);

            // The unauthorized response is reported rather than a copy of the body being made
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(content.SerializationCount, Is.EqualTo(0), "The oversized body must not have been read for a replay.");

            A.CallTo(() => fakeRequestHandler.Post(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .MustHaveHappened(1, Times.Exactly);
        }

        [Test]
        public async Task When_a_request_is_cancelled_while_the_reacquisition_waits_out_the_backoff_the_next_request_still_recovers()
        {
            var fakeRequestHandler = A.Fake<IFakeHttpRequestHandler>().SetBaseUrl(MockRequests.SourceApiBaseUrl);

            A.CallTo(() => fakeRequestHandler.Post(TokenUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(() => TokenResponse(FirstToken)).Once()
                .Then.ReturnsLazily(() => ServiceUnavailable()).Once()
                .Then.ReturnsLazily(() => TokenResponse(SecondToken));

            A.CallTo(() => fakeRequestHandler.Get(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(
                    (string url, HttpRequestMessage request) =>
                        request.Headers.Authorization?.Parameter == FirstToken ? Unauthorized() : Ok());

            TestHelpers.InitializeLogging();

            // A fake clock that is never advanced, so the request stays in its backoff delay until it is cancelled
            var clock = new FakeTimeProvider();

            using var apiClient = CreateApiClient(fakeRequestHandler, timeProvider: clock);

            using var cancellation = new CancellationTokenSource();

            var cancelledRequest = apiClient.HttpClient.GetAsync(ResourceRelativeUrl, cancellation.Token);

            // The failed re-acquisition (the second token request) is what puts the request into its backoff delay
            await WaitUntilAsync(() => CountTokenRequests(fakeRequestHandler) == 2);

            cancellation.Cancel();

            var caught = await CaptureAsync(cancelledRequest);

            Assert.That(caught, Is.InstanceOf<OperationCanceledException>(), $"Unexpected exception: {caught}");

            // The cancelled request let go of the token lock on its way out, so the next request re-acquires the token
            // itself and is replayed, with the token endpoint back
            var response = await apiClient.HttpClient.GetAsync(ResourceRelativeUrl);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            A.CallTo(() => fakeRequestHandler.Post(TokenUrl, A<HttpRequestMessage>.Ignored))
                .MustHaveHappened(3, Times.Exactly);
        }

        [Test]
        public async Task When_the_reacquisition_fails_at_first_the_request_waits_and_is_replayed_once_it_succeeds()
        {
            var fakeRequestHandler = A.Fake<IFakeHttpRequestHandler>().SetBaseUrl(MockRequests.SourceApiBaseUrl);

            // The token endpoint is briefly unavailable when the re-acquisition is first attempted
            A.CallTo(() => fakeRequestHandler.Post(TokenUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(() => TokenResponse(FirstToken)).Once()
                .Then.ReturnsLazily(() => ServiceUnavailable()).Once()
                .Then.ReturnsLazily(() => TokenResponse(SecondToken));

            A.CallTo(() => fakeRequestHandler.Get(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(
                    (string url, HttpRequestMessage request) =>
                        request.Headers.Authorization?.Parameter == FirstToken ? Unauthorized() : Ok());

            TestHelpers.InitializeLogging();

            var clock = new FakeTimeProvider();
            var startedAt = clock.GetUtcNow();

            using var apiClient = CreateApiClient(fakeRequestHandler, timeProvider: clock);

            var requestTask = apiClient.HttpClient.GetAsync(ResourceRelativeUrl);

            await clock.AdvanceUntilCompletedAsync(requestTask, BearerTokenRefreshPolicy.InitialRetryDelay);

            var response = await requestTask;

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "The request must have been replayed once the token was re-acquired.");

            // The request waited out the backoff rather than giving up on the first failed re-acquisition
            Assert.That(clock.GetUtcNow() - startedAt, Is.GreaterThanOrEqualTo(BearerTokenRefreshPolicy.InitialRetryDelay));

            // Initial acquisition, the failed re-acquisition, and the one that succeeded
            A.CallTo(() => fakeRequestHandler.Post(TokenUrl, A<HttpRequestMessage>.Ignored))
                .MustHaveHappened(3, Times.Exactly);

            // Sent once with the rejected token, replayed once with the new one
            A.CallTo(() => fakeRequestHandler.Get(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .MustHaveHappened(2, Times.Exactly);
        }

        [Test]
        public async Task When_the_token_cannot_be_reacquired_at_all_the_request_fails_as_an_authentication_failure()
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

            var clock = new FakeTimeProvider();

            using var apiClient = CreateApiClient(fakeRequestHandler, timeProvider: clock);

            var caught = await CaptureAsync(
                clock.AdvanceUntilCompletedAsync(
                    apiClient.HttpClient.GetAsync(ResourceRelativeUrl),
                    BearerTokenRefreshPolicy.InitialRetryDelay));

            // Not the unauthorized response, which a caller would record as one failed request and move on from
            Assert.That(
                EdFiApiAuthenticationException.IsRepresentedBy(caught),
                Is.True,
                $"Unexpected exception: {caught}");

            // The re-acquisition was retried as often as the policy allows before giving up
            A.CallTo(() => fakeRequestHandler.Post(TokenUrl, A<HttpRequestMessage>.Ignored))
                .MustHaveHappened(1 + BearerTokenRefreshPolicy.MaxConsecutiveFailuresWithoutUsableToken, Times.Exactly);

            // The request is not replayed when there is no usable token to replay it with
            A.CallTo(() => fakeRequestHandler.Get(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .MustHaveHappened(1, Times.Exactly);

            // And from here on, nothing is even sent
            caught = await CaptureAsync(apiClient.HttpClient.GetAsync(ResourceRelativeUrl));

            Assert.That(EdFiApiAuthenticationException.IsRepresentedBy(caught), Is.True, $"Unexpected exception: {caught}");

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
                BaseAddress = new Uri(MockRequests.SourceApiBaseUrl + "/")
            };

            var caught = await CaptureAsync(httpClient.GetAsync(ResourceRelativeUrl));

            Assert.That(caught, Is.Not.Null, "The request should not have been sent without a usable token.");
            Assert.That(
                EdFiApiAuthenticationException.IsRepresentedBy(caught),
                Is.True,
                $"Unexpected exception: {caught}");

            A.CallTo(() => fakeRequestHandler.Get(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .MustNotHaveHappened();
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            var timeout = Task.Delay(TimeSpan.FromSeconds(10));

            while (!condition())
            {
                Assert.That(timeout.IsCompleted, Is.False, "The awaited condition was not met in time.");

                await Task.Delay(10);
            }
        }

        private static int CountTokenRequests(IFakeHttpRequestHandler fakeRequestHandler) =>
            Fake.GetCalls(fakeRequestHandler).Count(call => call.Method.Name == "Post");

        private static async Task<Exception> CaptureAsync(Task task)
        {
            try
            {
                await task;
            }
            catch (Exception ex)
            {
                return ex;
            }

            return null;
        }

        private static EdFiApiClient CreateApiClient(
            IFakeHttpRequestHandler fakeRequestHandler,
            int bearerTokenRefreshMinutes = 60,
            TimeProvider timeProvider = null)
        {
            return new EdFiApiClient(
                "TestSource",
                TestHelpers.GetSourceApiConnectionDetails(),
                bearerTokenRefreshMinutes,
                ignoreSslErrors: true,
                new HttpClientHandlerFakeBridge(fakeRequestHandler),
                timeProvider);
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

        private static HttpResponseMessage ServiceUnavailable() =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };

        /// <summary>
        /// Request content that counts how many times its body is written out, which is how many times something
        /// has read it.
        /// </summary>
        private sealed class SerializationCountingContent : HttpContent
        {
            private readonly byte[] _body;
            private readonly long? _declaredLength;

            public SerializationCountingContent(string body, long? declaredLength = null)
            {
                _body = Encoding.UTF8.GetBytes(body);
                _declaredLength = declaredLength;
                Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            }

            public int SerializationCount { get; private set; }

            protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext context)
            {
                SerializationCount++;

                return stream.WriteAsync(_body, 0, _body.Length);
            }

            protected override bool TryComputeLength(out long length)
            {
                length = _declaredLength ?? _body.Length;

                return true;
            }
        }

        /// <summary>
        /// A forward-only stream, standing in for a body produced on the fly that cannot be sent a second time.
        /// </summary>
        private sealed class NonSeekableStream : Stream
        {
            private readonly MemoryStream _inner;

            public NonSeekableStream(byte[] content)
            {
                _inner = new MemoryStream(content, writable: false);
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                _inner.ReadAsync(buffer, offset, count, cancellationToken);

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
