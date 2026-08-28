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
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// Covers the behavior the ticket is ultimately about over a run longer than a token: publishing has to continue
    /// across the token's expiry, across a token endpoint that is unavailable for a while, and across the replacement
    /// of the token, without a document being lost. The first test compresses hours of a run into a fake clock; the
    /// second drives a real publishing run through both API clients.
    /// </summary>
    [TestFixture]
    public class BearerTokenLongRunTests
    {
        private const string TokenUrl = MockRequests.SourceApiBaseUrl + "/oauth/token";
        private const string ResourceUrl = MockRequests.SourceApiBaseUrl + "/data/v3/ed-fi/schools";
        private const string ResourceRelativeUrl = "data/v3/ed-fi/schools";
        private const string StateEducationAgencies = "/ed-fi/stateEducationAgencies";

        [Test]
        public async Task Publishing_continues_for_hours_across_token_expiry_a_token_endpoint_outage_and_token_replacement()
        {
            const int TokenLifetimeSeconds = 600;
            var runLength = TimeSpan.FromHours(6);

            var clock = new FakeTimeProvider();
            var startedAt = clock.GetUtcNow();

            // The token endpoint is unavailable for three minutes, starting shortly after a refresh is due. The
            // refresh at that point fails and has to be retried until the endpoint is back, all while the token in
            // hand is still valid.
            var outageStart = startedAt + TimeSpan.FromMinutes(130) - TimeSpan.FromSeconds(10);
            var outageEnd = outageStart + TimeSpan.FromMinutes(3);

            var fakeRequestHandler = A.Fake<IFakeHttpRequestHandler>().SetBaseUrl(MockRequests.SourceApiBaseUrl);

            // An API that issues tokens with a lifetime and rejects a token once that lifetime has elapsed
            var tokenExpiries = new Dictionary<string, DateTimeOffset>();
            int tokensIssued = 0;
            int tokenRequestsRefused = 0;

            A.CallTo(() => fakeRequestHandler.Post(TokenUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(
                    () =>
                    {
                        var now = clock.GetUtcNow();

                        if (now >= outageStart && now < outageEnd)
                        {
                            tokenRequestsRefused++;

                            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                            {
                                Content = new StringContent("{}", Encoding.UTF8, "application/json")
                            };
                        }

                        string token = $"token-{++tokensIssued}";
                        tokenExpiries[token] = now.AddSeconds(TokenLifetimeSeconds);

                        return FakeResponse.OK(new { access_token = token, expires_in = TokenLifetimeSeconds });
                    });

            A.CallTo(() => fakeRequestHandler.Get(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(
                    (string url, HttpRequestMessage request) =>
                    {
                        string token = request.Headers.Authorization?.Parameter;

                        bool tokenIsValid = token != null
                            && tokenExpiries.TryGetValue(token, out var expiresAt)
                            && clock.GetUtcNow() < expiresAt;

                        return tokenIsValid
                            ? FakeResponse.OK(new { })
                            : new HttpResponseMessage(HttpStatusCode.Unauthorized)
                            {
                                Content = new StringContent("{\"error\":\"invalid_token\"}", Encoding.UTF8, "application/json")
                            };
                    });

            TestHelpers.InitializeLogging();

            using var apiClient = new EdFiApiClient(
                "TestSource",
                TestHelpers.GetSourceApiConnectionDetails(),
                bearerTokenRefreshMinutes: 28,
                ignoreSslErrors: true,
                new HttpClientHandlerFakeBridge(fakeRequestHandler),
                clock);

            // A request every minute for the length of the run, each of which has to succeed
            var failures = new List<string>();
            var step = TimeSpan.FromMinutes(1);

            for (var elapsed = step; elapsed <= runLength; elapsed += step)
            {
                clock.Advance(step);

                var response = await apiClient.HttpClient.GetAsync(ResourceRelativeUrl);

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    failures.Add($"{elapsed}: {response.StatusCode}");
                }
            }

            Assert.That(failures, Is.Empty, "Every request over the run must have succeeded.");

            // The token was refreshed at half its lifetime throughout, which is what kept the requests authenticated
            int expectedRefreshes = (int)(runLength.TotalSeconds / (TokenLifetimeSeconds / 2));
            Assert.That(tokensIssued, Is.GreaterThanOrEqualTo(expectedRefreshes), "The token should have been replaced every five minutes.");

            // The outage was actually hit, retried through, and recovered from
            Assert.That(tokenRequestsRefused, Is.GreaterThan(1), "The outage should have been retried more than once.");

            // No request was ever rejected: the refresh always ran ahead of the expiry
            A.CallTo(() => fakeRequestHandler.Get(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .MustHaveHappened((int)(runLength / step), Times.Exactly);
        }

        [Test]
        public async Task When_both_tokens_are_rejected_mid_run_every_document_is_still_published()
        {
            const int DocumentCount = 25;
            const string InitialSourceToken = "source-token-1";
            const string ReplacementSourceToken = "source-token-2";
            const string InitialTargetToken = "target-token-1";
            const string ReplacementTargetToken = "target-token-2";

            var fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler()
                .AvailableChangeVersions(1100)
                .ResourceCount(responseTotalCountHeader: DocumentCount)
                .GetResourceData(
                    $"{EdFiApiConstants.DataManagementApiSegment}{StateEducationAgencies}",
                    TestHelpers.GetGenericResourceFaker().Generate(DocumentCount));

            GivenTheTokenEndpointReplacesTheToken(
                fakeSourceRequestHandler, MockRequests.SourceApiBaseUrl, InitialSourceToken, ReplacementSourceToken);

            // The source accepts its first token for the metadata requests, then rejects it on the page request,
            // which is where an expiry lands mid-run
            string sourceResourceUrl = $"{MockRequests.SourceApiBaseUrl}{MockRequests.DataManagementPath}{StateEducationAgencies}";

            A.CallTo(() => fakeSourceRequestHandler.Get(
                    sourceResourceUrl,
                    A<HttpRequestMessage>.That.Matches(
                        request => IsPageRequest(request) && CarriesToken(request, InitialSourceToken))))
                .ReturnsLazily(() => Unauthorized());

            var fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();
            fakeTargetRequestHandler.EveryDataManagementPostReturns200Ok();

            GivenTheTokenEndpointReplacesTheToken(
                fakeTargetRequestHandler, MockRequests.TargetApiBaseUrl, InitialTargetToken, ReplacementTargetToken);

            // The target rejects its first token on the documents themselves
            string targetResourceUrl = $"{MockRequests.TargetApiBaseUrl}{MockRequests.DataManagementPath}{StateEducationAgencies}";

            A.CallTo(() => fakeTargetRequestHandler.Post(
                    targetResourceUrl,
                    A<HttpRequestMessage>.That.Matches(request => CarriesToken(request, InitialTargetToken))))
                .ReturnsLazily(() => Unauthorized());

            var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(include: new[] { StateEducationAgencies });
            var targetApiConnectionDetails = TestHelpers.GetTargetApiConnectionDetails();

            var options = TestHelpers.GetOptions();
            options.IncludeDescriptors = false;

            TestHelpers.InitializeLogging();

            var changeProcessor = TestHelpers.CreateChangeProcessorWithDefaultDependencies(
                options,
                sourceApiConnectionDetails,
                fakeSourceRequestHandler,
                targetApiConnectionDetails,
                fakeTargetRequestHandler);

            await changeProcessor.ProcessChangesAsync(
                TestHelpers.CreateChangeProcessorConfiguration(options),
                CancellationToken.None);

            // Every document reached the target, under the replacement token
            A.CallTo(() => fakeTargetRequestHandler.Post(
                    targetResourceUrl,
                    A<HttpRequestMessage>.That.Matches(request => CarriesToken(request, ReplacementTargetToken))))
                .MustHaveHappened(DocumentCount, Times.Exactly);

            // Each side replaced its token exactly once: the initial acquisition and one re-acquisition
            A.CallTo(() => fakeSourceRequestHandler.Post($"{MockRequests.SourceApiBaseUrl}/oauth/token", A<HttpRequestMessage>.Ignored))
                .MustHaveHappened(2, Times.Exactly);
            A.CallTo(() => fakeTargetRequestHandler.Post($"{MockRequests.TargetApiBaseUrl}/oauth/token", A<HttpRequestMessage>.Ignored))
                .MustHaveHappened(2, Times.Exactly);
        }

        private static void GivenTheTokenEndpointReplacesTheToken(
            IFakeHttpRequestHandler fakeRequestHandler,
            string apiBaseUrl,
            string initialToken,
            string replacementToken)
        {
            A.CallTo(() => fakeRequestHandler.Post($"{apiBaseUrl}/oauth/token", A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(() => FakeResponse.OK(new { access_token = initialToken }))
                .Once()
                .Then.ReturnsLazily(() => FakeResponse.OK(new { access_token = replacementToken }));
        }

        private static bool IsPageRequest(HttpRequestMessage request) =>
            !request.RequestUri.Query.Contains("totalCount", StringComparison.OrdinalIgnoreCase);

        private static bool CarriesToken(HttpRequestMessage request, string token) =>
            request.Headers.Authorization?.Parameter == token;

        private static HttpResponseMessage Unauthorized() =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":\"invalid_token\"}", Encoding.UTF8, "application/json")
            };
    }
}
