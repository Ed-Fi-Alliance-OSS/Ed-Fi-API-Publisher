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
using Shouldly;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    [TestFixture]
    public class EdFiClientProviderTests
    {


        [Test]
        public async Task RefusesADependenciesUrlThatWouldSendTheTokenToAnotherHost()
        {
            // Measured: Uri("https://api.example//evil.test/deps").AbsolutePath is "//evil.test/deps", and an
            // HttpClient resolves that against its base address as https://evil.test/deps, a network-path
            // reference rather than a path. BearerTokenHandler stamps the connection's token on it.
            var apiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails();

            var fakeRequestHandler = A.Fake<IFakeHttpRequestHandler>()
                .SetBaseUrl(MockRequests.SourceApiBaseUrl)
                .OAuthToken()
                .ApiVersionMetadataUrls(
                    apiVersion: "6.1",
                    edfiVersion: "4.0.0",
                    urls: new Dictionary<string, string>
                    {
                        { "dependencies", $"{MockRequests.SourceApiBaseUrl}//evil.test/test-dependencies-path" }
                    });

            using var client = new EdFiApiClient(
                "TestClient", apiConnectionDetails, 60, false, new HttpClientHandlerFakeBridge(fakeRequestHandler));

            var clientProvider = A.Fake<IEdFiApiClientProvider>();
            A.CallTo(() => clientProvider.GetApiClient()).Returns(client);

            var exception = await Should.ThrowAsync<InvalidConfigurationException>(
                async () => await clientProvider.GetEdFiUrlFromMetadataOrDefaultAsync("dependencies"));

            exception.Message.ShouldContain("evil.test");
        }

        [Test]
        public async Task RefusesADependenciesUrlOnAnotherHostOutright()
        {
            var apiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails();

            var fakeRequestHandler = A.Fake<IFakeHttpRequestHandler>()
                .SetBaseUrl(MockRequests.SourceApiBaseUrl)
                .OAuthToken()
                .ApiVersionMetadataUrls(
                    apiVersion: "6.1",
                    edfiVersion: "4.0.0",
                    urls: new Dictionary<string, string>
                    {
                        { "dependencies", "https://elsewhere.example/test-dependencies-path" }
                    });

            using var client = new EdFiApiClient(
                "TestClient", apiConnectionDetails, 60, false, new HttpClientHandlerFakeBridge(fakeRequestHandler));

            var clientProvider = A.Fake<IEdFiApiClientProvider>();
            A.CallTo(() => clientProvider.GetApiClient()).Returns(client);

            var exception = await Should.ThrowAsync<InvalidConfigurationException>(
                async () => await clientProvider.GetEdFiUrlFromMetadataOrDefaultAsync("dependencies"));

            exception.Message.ShouldContain("not served by the host this connection addresses");
        }

        [Test]
        public async Task RefusesDependenciesFallback_WhenDataManagementIsServedAboveTheConnection()
        {
            // An API that declares its data management outside the prefix the connection carries resolves to
            // a segment that climbs out of it. Composed into the conventional location, the ".." cancels the
            // "metadata" element rather than sitting inside it, so a connection at "/edfi/" with a segment of
            // "../other/data" would request "https://server/edfi/other/data/dependencies", which names
            // nothing. Measured before this guard existed.
            var apiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails();
            apiConnectionDetails.Url = MockRequests.SourceApiBaseUrl + "/edfi/";

            var fakeRequestHandler = A.Fake<IFakeHttpRequestHandler>()
                .SetBaseUrl(MockRequests.SourceApiBaseUrl + "/edfi")
                .OAuthToken()
                .ApiVersionMetadataUrls(
                    apiVersion: "6.1",
                    edfiVersion: "4.0.0",
                    urls: new Dictionary<string, string>
                    {
                        // Declared on the same host, but above the address the connection uses, and with no
                        // dependencies URL of its own, which is what reaches the fallback.
                        { "dataManagementApi", MockRequests.SourceApiBaseUrl + "/other/data" }
                    });

            using var client = new EdFiApiClient(
                "TestClient", apiConnectionDetails, 60, false, new HttpClientHandlerFakeBridge(fakeRequestHandler));

            client.DataManagementApiSegment.ShouldBe("../other/data");

            var clientProvider = A.Fake<IEdFiApiClientProvider>();
            A.CallTo(() => clientProvider.GetApiClient()).Returns(client);

            var exception = await Should.ThrowAsync<InvalidConfigurationException>(
                async () => await clientProvider.GetEdFiUrlFromMetadataOrDefaultAsync("dependencies"));

            exception.Message.ShouldContain("served above the address this connection uses");
        }

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
