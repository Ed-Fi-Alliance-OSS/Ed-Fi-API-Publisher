// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using NUnit.Framework;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    [TestFixture]
    public class EdFiApiClientTests
    {


        [Test]
        public void TokenRequequest_ShouldAuthenticateWithAPIBaseUrl()
        {
            // Arrange
            // No Auth Url
            var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails();

            var fakeRequestHandler = A.Fake<IFakeHttpRequestHandler>()
                .SetBaseUrl(MockRequests.SourceApiBaseUrl)
                .OAuthToken();
            var client = new EdFiApiClient("TestClient", sourceApiConnectionDetails, 60, false, new HttpClientHandlerFakeBridge(fakeRequestHandler));

            // Act
            var authHeader = client.HttpClient.DefaultRequestHeaders.Authorization;

            // Assert
            Assert.That(authHeader != null, "Authentication Header cannot be null");
            Assert.That(authHeader.Scheme, Is.EqualTo("Bearer"));
            Assert.That(authHeader.Parameter, Is.EqualTo(MockRequests.OdsApiToken));

        }

        [Test]
        public void TokenRequequest_ShouldAuthenticateWithAuthUrl()
        {
            // Arrange
            // AuthUrl is passed
            var apiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails();
            apiConnectionDetails.AuthUrl = MockRequests.SourceAuthenticateServiceUrl;

            //var _fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler();
            var fakeRequestHandler = A.Fake<IFakeHttpRequestHandler>()
                .SetBaseUrl(apiConnectionDetails.AuthUrl)
                .SeparateAuthServiceToken();

            var client = new EdFiApiClient("TestClient", apiConnectionDetails, 60, false, new HttpClientHandlerFakeBridge(fakeRequestHandler));

            // Act
            var authHeader = client.HttpClient.DefaultRequestHeaders.Authorization;

            // Assert
            Assert.That(authHeader != null, "Authentication Header cannot be null");
            Assert.That(authHeader.Scheme, Is.EqualTo("Bearer"));
            Assert.That(authHeader.Parameter, Is.EqualTo(MockRequests.AuthServiceToken));

        }

    }

}
