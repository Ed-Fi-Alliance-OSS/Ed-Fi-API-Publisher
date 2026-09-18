// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using NUnit.Framework;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    [TestFixture]
    public class EdFiClientProviderTests
    {


        [Test]
        public async Task ReturnsDependenciesUrl_WhenAvailableInMetadata()
        {
            // Arrange

            var apiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails();
            IEdFiApiClientProvider _fakeClientProvider = A.Fake<IEdFiApiClientProvider>();
            var fakeRequestHandler = A.Fake<IFakeHttpRequestHandler>()
                .SetBaseUrl(MockRequests.SourceApiBaseUrl)
                .OAuthToken()
                .ApiVersionMetadataUrls(
                                apiVersion: "6.1",
                                edfiVersion: "4.0.0",
                                urls: new Dictionary<string, string> {
                                        { "dependencies", "https://test.source/test-dependencies-path" }
                                }
            );

            EdFiApiClient _fakeClient = new EdFiApiClient("TestClient", apiConnectionDetails, 60, false, new HttpClientHandlerFakeBridge(fakeRequestHandler));

            A.CallTo(() => _fakeClientProvider.GetApiClient()).Returns(_fakeClient);
            // Act
            var result = await _fakeClientProvider.GetEdFiUrlFromMetadataOrDefaultAsync("dependencies");

            // Assert
            Assert.That(result, Is.EqualTo("/test-dependencies-path"));
        }

        [Test]
        public async Task ReturnsDependenciesUrl_NotAvailableInMetadata_ReturnDefaultValue()
        {
            // Arrange

            var apiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails();
            IEdFiApiClientProvider _fakeClientProvider = A.Fake<IEdFiApiClientProvider>();
            var fakeRequestHandler = A.Fake<IFakeHttpRequestHandler>()
                .SetBaseUrl(MockRequests.SourceApiBaseUrl)
                .OAuthToken()
                .ApiVersionMetadataUrls(
                                apiVersion: "6.1",
                                edfiVersion: "4.0.0",
                                urls: new Dictionary<string, string> {
                                        { "dependenciesUrlNotInVersion", "https://test.source/test-dependencies-path" }
                                }
            );

            EdFiApiClient _fakeClient = new EdFiApiClient("TestClient", apiConnectionDetails, 60, false, new HttpClientHandlerFakeBridge(fakeRequestHandler));

            A.CallTo(() => _fakeClientProvider.GetApiClient()).Returns(_fakeClient);
            // Act
            var result = await _fakeClientProvider.GetEdFiUrlFromMetadataOrDefaultAsync("dependencies");

            // Assert default value for dependencies
            Assert.That(result, Is.EqualTo("metadata/data/v3/dependencies"));
        }

        [Test]
        public void RefusesDependenciesUrl_WhenMetadataStillCarriesARoutePlaceholder()
        {
            // An API that qualifies its routes answers the unqualified address with placeholders rather than
            // values. Requesting one draws a 404 naming a URL with '%7B' in it, because Uri escapes the braces.

            var apiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails();
            IEdFiApiClientProvider fakeClientProvider = A.Fake<IEdFiApiClientProvider>();
            var fakeRequestHandler = A.Fake<IFakeHttpRequestHandler>()
                .SetBaseUrl(MockRequests.SourceApiBaseUrl)
                .OAuthToken()
                .ApiVersionMetadataUrls(
                    apiVersion: "8.0",
                    edfiVersion: "5.2.0",
                    urls: new Dictionary<string, string> {
                        { "dependencies", "https://test.source/{districtId}/metadata/dependencies" }
                    }
            );

            using EdFiApiClient fakeClient = new("TestClient", apiConnectionDetails, 60, false, new HttpClientHandlerFakeBridge(fakeRequestHandler));

            A.CallTo(() => fakeClientProvider.GetApiClient()).Returns(fakeClient);

            Func<Task> resolvingTheUrl =
                () => fakeClientProvider.GetEdFiUrlFromMetadataOrDefaultAsync("dependencies");

            var exception = Assert.ThrowsAsync<InvalidConfigurationException>(resolvingTheUrl);

            Assert.That(exception.Message, Does.Contain("route placeholder"));
            Assert.That(exception.Message, Does.Contain("districtId"));
        }
    }
}
