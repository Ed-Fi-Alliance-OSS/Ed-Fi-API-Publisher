// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Connections.Api.Helpers;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using NUnit.Framework;
using Shouldly;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// Verifies that a configured Profile is applied to resource reads but not to the cursor paging
    /// '/partitions' request (APIPUB-139): a page token list is not a resource representation, so requesting
    /// it under a Profile content type would be meaningless and could be rejected by the source.
    /// </summary>
    [TestFixture]
    public class RequestHelpersProfileTests
    {
        private const string ProfileName = "Unit-Test-Source-Profile";
        private const string ResourceUrl = "/ed-fi/students";

        private static (EdFiApiClient client, IFakeHttpRequestHandler fake) CreateClient()
        {
            var fake = TestHelpers.GetFakeBaselineSourceApiRequestHandler();

            var client = new EdFiApiClient(
                "TestSource",
                TestHelpers.GetSourceApiConnectionDetails(profileName: ProfileName),
                bearerTokenRefreshMinutes: 27,
                ignoreSslErrors: true,
                httpClientHandler: new HttpClientHandlerFakeBridge(fake));

            return (client, fake);
        }

        private static async Task<HttpRequestMessage> SendAndCaptureAsync(string localPath, string requestUri)
        {
            var (client, fake) = CreateClient();

            HttpRequestMessage capturedRequest = null;

            A.CallTo(
                    () => fake.Get(
                        A<string>.Ignored,
                        A<HttpRequestMessage>.That.Matches(msg => msg.RequestUri.LocalPath == localPath)))
                .ReturnsLazily(
                    (string _, HttpRequestMessage request) =>
                    {
                        capturedRequest = request;

                        return FakeResponse.OK("[]");
                    });

            using var response = await RequestHelpers.SendGetRequestAsync(client, ResourceUrl, requestUri, CancellationToken.None);

            capturedRequest.ShouldNotBeNull();

            return capturedRequest;
        }

        private static bool HasEdFiProfileAcceptHeader(HttpRequestMessage request) =>
            request.Headers.Accept.Any(h => h.MediaType is not null && h.MediaType.Contains("vnd.ed-fi"));

        [Test]
        public async Task Resource_page_request_should_carry_the_readable_profile_content_type()
        {
            var request = await SendAndCaptureAsync(
                "/data/v3/ed-fi/students",
                "data/v3/ed-fi/students?offset=0&limit=1");

            HasEdFiProfileAcceptHeader(request).ShouldBeTrue();

            request.Headers.Accept.ToString()
                .ShouldBe($"application/vnd.ed-fi.student.{ProfileName.ToLower()}.readable+json");
        }

        [Test]
        public async Task Partitions_request_should_not_carry_a_profile_content_type()
        {
            var request = await SendAndCaptureAsync(
                "/data/v3/ed-fi/students/partitions",
                "data/v3/ed-fi/students/partitions?number=1");

            HasEdFiProfileAcceptHeader(request).ShouldBeFalse();
        }
    }
}
