// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
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

    }
}
