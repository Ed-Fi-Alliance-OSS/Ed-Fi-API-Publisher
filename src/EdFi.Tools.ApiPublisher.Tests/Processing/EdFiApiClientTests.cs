// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using NUnit.Framework;

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
                "TestClient", apiConnectionDetails, 60, new HttpClientHandlerFakeBridge(fakeRequestHandler));

            // Assert
            Assert.That(tokenManager.CurrentBearerToken, Is.EqualTo(MockRequests.AuthServiceToken));
        }
    }
}
