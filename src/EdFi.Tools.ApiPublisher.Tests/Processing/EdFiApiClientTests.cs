// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using NUnit.Framework;
using Serilog.Sinks.TestCorrelator;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    [TestFixture]
    public class EdFiApiClientTests
    {
        private const string ResourceUrl = MockRequests.SourceApiBaseUrl + "/data/v3/ed-fi/schools";
        private const string ResourceRelativeUrl = "data/v3/ed-fi/schools";

        [Test]
        public async Task TokenRequest_ShouldAuthenticateWithAPIBaseUrl()
        {
            // Arrange
            // No Auth Url
            var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails();

            var fakeRequestHandler = A.Fake<IFakeHttpRequestHandler>()
                .SetBaseUrl(MockRequests.SourceApiBaseUrl)
                .OAuthToken();

            string appliedAuthorizationHeader = null;

            A.CallTo(() => fakeRequestHandler.Get(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(
                    (string url, HttpRequestMessage request) =>
                    {
                        appliedAuthorizationHeader = request.Headers.Authorization?.ToString();

                        return FakeResponse.OK(new { });
                    });

            TestHelpers.InitializeLogging();

            using var client = new EdFiApiClient(
                "TestClient", sourceApiConnectionDetails, 60, false, new HttpClientHandlerFakeBridge(fakeRequestHandler));

            // Act
            await client.HttpClient.GetAsync(ResourceRelativeUrl);

            // Assert
            // The token is obtained from the API's own base URL and reaches the API on the request itself, applied
            // by the request pipeline
            Assert.That(appliedAuthorizationHeader, Is.EqualTo($"Bearer {MockRequests.OdsApiToken}"));
        }

        [Test]
        public async Task A_stated_authentication_url_should_reach_the_resolver_from_the_connection()
        {
            // Resolution moved out of BearerTokenManager, which used to read AuthUrl itself and had a test
            // that proved it. Nothing replaced that: passing null instead of the connection's AuthUrl here
            // leaves the whole suite green while --sourceAuthUrl and --targetAuthUrl quietly stop working.
            const string StatedUrl = MockRequests.SourceApiBaseUrl + "/identity/connect/token";
            const string DeclaredUrl = MockRequests.SourceApiBaseUrl + "/declared/token";

            var connectionDetails = TestHelpers.GetSourceApiConnectionDetails();
            connectionDetails.AuthUrl = StatedUrl;

            var fakeRequestHandler = A.Fake<IFakeHttpRequestHandler>()
                .SetBaseUrl(MockRequests.SourceApiBaseUrl)
                .ApiVersionMetadataUrls(
                    "5.2",
                    "3.3.0-a",
                    new Dictionary<string, string>
                    {
                        ["dataManagementApi"] = MockRequests.SourceApiBaseUrl + "/data/v3/",
                        ["changeQueries"] = MockRequests.SourceApiBaseUrl + "/changeQueries/v1/",
                        ["oauth"] = DeclaredUrl
                    });

            A.CallTo(() => fakeRequestHandler.Post(StatedUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(() => FakeResponse.OK(new { access_token = MockRequests.OdsApiToken }));

            A.CallTo(() => fakeRequestHandler.Get(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(() => FakeResponse.OK(new { }));

            TestHelpers.InitializeLogging();

            using var client = new EdFiApiClient(
                "TestClient", connectionDetails, 60, false, new HttpClientHandlerFakeBridge(fakeRequestHandler));

            await client.HttpClient.GetAsync(ResourceRelativeUrl);

            // The stated URL wins over the declared one, which is the precedence the connection setting buys.
            A.CallTo(() => fakeRequestHandler.Post(StatedUrl, A<HttpRequestMessage>.Ignored))
                .MustHaveHappened();

            A.CallTo(() => fakeRequestHandler.Post(DeclaredUrl, A<HttpRequestMessage>.Ignored))
                .MustNotHaveHappened();
        }

        [Test]
        public void The_certificate_rule_should_reach_the_resolver_from_the_client()
        {
            // ignoreSslErrors is the stated basis for refusing an endpoint the API nominated on another
            // host. Roughly thirty tests build a client with it on, but every one of them declares oauth at
            // the connection's own address, where the resolver returns before the rule is consulted. Hard
            // coding the flag to false fails none of them.
            var fakeRequestHandler = A.Fake<IFakeHttpRequestHandler>()
                .SetBaseUrl(MockRequests.SourceApiBaseUrl)
                .ApiVersionMetadataUrls(
                    "5.2",
                    "3.3.0-a",
                    new Dictionary<string, string>
                    {
                        ["dataManagementApi"] = MockRequests.SourceApiBaseUrl + "/data/v3/",
                        ["changeQueries"] = MockRequests.SourceApiBaseUrl + "/changeQueries/v1/",
                        ["oauth"] = "https://identity.elsewhere/token"
                    });

            TestHelpers.InitializeLogging();

            // Bound through an Action so the call is not ambiguous with the obsolete TestDelegate overload.
            Action buildClient = () => new EdFiApiClient(
                "TestClient",
                TestHelpers.GetSourceApiConnectionDetails(),
                60,
                ignoreSslErrors: true,
                new HttpClientHandlerFakeBridge(fakeRequestHandler));

            var thrown = Assert.Throws<InvalidConfigurationException>(buildClient);

            Assert.That(thrown.Message, Does.Contain("--ignoreSslErrors"));
        }

        [Test]
        [TestCase("https://elsewhere.test/", true, TestName = "a redirect to another host is reported")]
        [TestCase("https://test.source:8443/", true, TestName = "a redirect to another port is reported")]
        [TestCase("http://test.source/", false, TestName = "the same address over another scheme is not")]
        public void A_Discovery_document_served_by_another_address_should_be_reported(
            string servedBy,
            bool shouldWarn)
        {
            // The handler follows redirects, so the document may come from somewhere else, and what is read
            // out of it is judged against the address the operator configured rather than the one that
            // answered. HttpClient records the final address on RequestMessage; the fake does not set it,
            // so it is set here the way a real redirect would leave it.
            //
            // The connection is at "https://test.source". The third case differs from it by scheme alone,
            // which is the ordinary upgrade a root performs and must pass without a word; comparing the
            // whole URL rather than the authority would warn on it, and that is what this case pins.
            var fakeRequestHandler = A.Fake<IFakeHttpRequestHandler>()
                .SetBaseUrl(MockRequests.SourceApiBaseUrl)
                .SetDataManagementUrlSegment("data/v3")
                .SetChangeQueriesUrlSegment("changeQueries/v1")
                .OAuthToken()
                .ApiVersionMetadata();

            A.CallTo(() => fakeRequestHandler.Get($"{MockRequests.SourceApiBaseUrl}/", A<HttpRequestMessage>.Ignored))
                .ReturnsLazily((string url, HttpRequestMessage request) =>
                {
                    var response = MockRequests.DiscoveryDocumentFor(fakeRequestHandler);
                    response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, new Uri(servedBy));

                    return response;
                });

            TestHelpers.InitializeLogging();

            using (TestCorrelator.CreateContext())
            {
                using var client = new EdFiApiClient(
                    "TestClient",
                    TestHelpers.GetSourceApiConnectionDetails(),
                    60,
                    false,
                    new HttpClientHandlerFakeBridge(fakeRequestHandler));

                Assert.That(
                    TestCorrelator.GetLogEventsFromCurrentContext()
                        .Any(e => e.MessageTemplate.Text.Contains("was served by")),
                    Is.EqualTo(shouldWarn));
            }
        }

        [Test]
        public void The_Discovery_document_should_be_read_once_for_a_client()
        {
            // The stated efficiency of reading paths from the document rests on this: one read per client,
            // shared by the version check and the dependency metadata. Nothing asserted it, so a read that
            // happened per segment, or a retry that fired on success, would pass every other test here.
            var fakeRequestHandler = A.Fake<IFakeHttpRequestHandler>()
                .SetBaseUrl(MockRequests.SourceApiBaseUrl)
                .SetDataManagementUrlSegment("data/v3")
                .SetChangeQueriesUrlSegment("changeQueries/v1")
                .OAuthToken()
                .ApiVersionMetadata();

            TestHelpers.InitializeLogging();

            using var client = new EdFiApiClient(
                "TestClient",
                TestHelpers.GetSourceApiConnectionDetails(),
                60,
                false,
                new HttpClientHandlerFakeBridge(fakeRequestHandler));

            // Both segments are read, so a per-segment read would show up as more than one call.
            _ = client.DataManagementApiSegment;
            _ = client.ChangeQueriesApiSegment;

            A.CallTo(() => fakeRequestHandler.Get(
                    $"{MockRequests.SourceApiBaseUrl}/",
                    A<HttpRequestMessage>.Ignored))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task A_token_should_be_requested_from_the_endpoint_the_api_declares()
        {
            // Arrange
            // The declared endpoint is deliberately not the conventional path, so that composing that path
            // rather than reading the Discovery document would show up here.
            const string DeclaredTokenUrl = MockRequests.SourceApiBaseUrl + "/identity/connect/token";

            var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails();

            var fakeRequestHandler = A.Fake<IFakeHttpRequestHandler>()
                .SetBaseUrl(MockRequests.SourceApiBaseUrl)
                .ApiVersionMetadataUrls(
                    "5.2",
                    "3.3.0-a",
                    new Dictionary<string, string>
                    {
                        ["dataManagementApi"] = MockRequests.SourceApiBaseUrl + "/data/v3/",
                        ["changeQueries"] = MockRequests.SourceApiBaseUrl + "/changeQueries/v1/",
                        ["oauth"] = DeclaredTokenUrl
                    });

            A.CallTo(() => fakeRequestHandler.Post(DeclaredTokenUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(() => FakeResponse.OK(new { access_token = MockRequests.OdsApiToken }));

            string appliedAuthorizationHeader = null;

            A.CallTo(() => fakeRequestHandler.Get(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(
                    (string url, HttpRequestMessage request) =>
                    {
                        appliedAuthorizationHeader = request.Headers.Authorization?.ToString();

                        return FakeResponse.OK(new { });
                    });

            TestHelpers.InitializeLogging();

            using var client = new EdFiApiClient(
                "TestClient", sourceApiConnectionDetails, 60, false, new HttpClientHandlerFakeBridge(fakeRequestHandler));

            // Act
            await client.HttpClient.GetAsync(ResourceRelativeUrl);

            // Assert
            Assert.That(appliedAuthorizationHeader, Is.EqualTo($"Bearer {MockRequests.OdsApiToken}"));

            A.CallTo(
                    () => fakeRequestHandler.Post(
                        $"{MockRequests.SourceApiBaseUrl}/oauth/token",
                        A<HttpRequestMessage>.Ignored))
                .MustNotHaveHappened();
        }

        [Test]
        public async Task Requests_identify_the_publisher_and_its_runtime_in_the_user_agent()
        {
            var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails();

            var fakeRequestHandler = A.Fake<IFakeHttpRequestHandler>()
                .SetBaseUrl(MockRequests.SourceApiBaseUrl)
                .OAuthToken();

            System.Net.Http.Headers.HttpHeaderValueCollection<System.Net.Http.Headers.ProductInfoHeaderValue> userAgent = null;

            A.CallTo(() => fakeRequestHandler.Get(ResourceUrl, A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(
                    (string url, HttpRequestMessage request) =>
                    {
                        userAgent = request.Headers.UserAgent;

                        return FakeResponse.OK(new { });
                    });

            TestHelpers.InitializeLogging();

            using var client = new EdFiApiClient(
                "TestClient", sourceApiConnectionDetails, 60, false, new HttpClientHandlerFakeBridge(fakeRequestHandler));

            await client.HttpClient.GetAsync(ResourceRelativeUrl);

            var products = userAgent.Select(value => value.Product).ToList();

            Assert.That(products.Select(product => product.Name), Does.Contain("Ed-Fi-API-Publisher"));

            // The runtime, as ".NET/10.0" or the like, split off the framework display name
            var runtime = products.SingleOrDefault(product => product.Name.StartsWith(".NET"));

            Assert.That(runtime, Is.Not.Null, $"User agent: {string.Join(" ", userAgent)}");
            Assert.That(runtime.Version, Does.Match(@"^\d+\.\d+"));
        }

        [Test]
        public void TokenRequequest_ShouldAuthenticateWithAuthUrl()
        {
            // Arrange
            // AuthUrl is passed
            var apiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails();
            apiConnectionDetails.AuthUrl = MockRequests.SourceAuthenticateServiceUrl;

            var fakeRequestHandler = A.Fake<IFakeHttpRequestHandler>()
                .SetBaseUrl(apiConnectionDetails.AuthUrl)
                .SeparateAuthServiceToken();

            TestHelpers.InitializeLogging();

            using var tokenManager = new BearerTokenManager(
                "TestClient",
                apiConnectionDetails,
                60,
                new HttpClientHandlerFakeBridge(fakeRequestHandler),
                new Uri(apiConnectionDetails.AuthUrl));

            // Assert
            Assert.That(tokenManager.CurrentBearerToken, Is.EqualTo(MockRequests.AuthServiceToken));
        }
    }
}
