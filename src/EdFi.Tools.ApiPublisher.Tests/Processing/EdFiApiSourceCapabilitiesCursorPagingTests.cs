// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Connections.Api.Metadata.Versioning;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Capabilities;
using EdFi.Tools.ApiPublisher.Tests.Extensions;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using NUnit.Framework;
using Serilog.Events;
using Serilog.Sinks.TestCorrelator;
using Shouldly;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// Verifies cursor paging detection (APIPUB-139): a source version of 7.3 or later AND a successful
    /// GET /{resource}/partitions?number=1 probe returning a pageTokens array. Anything else means offset paging,
    /// and the answer is determined once per run.
    /// </summary>
    [TestFixture]
    public class EdFiApiSourceCapabilitiesCursorPagingTests
    {
        private const string ProbePath = "/data/v3/ed-fi/students/partitions";

        private static (EdFiApiSourceCapabilities capabilities, IFakeHttpRequestHandler fake) Create(string apiVersion)
        {
            var fake = TestHelpers.GetFakeBaselineSourceApiRequestHandler().ApiVersionMetadata(apiVersion: apiVersion);

            EdFiApiClient ClientFactory() =>
                new EdFiApiClient(
                    "TestSource",
                    TestHelpers.GetSourceApiConnectionDetails(),
                    bearerTokenRefreshMinutes: 27,
                    ignoreSslErrors: true,
                    httpClientHandler: new HttpClientHandlerFakeBridge(fake));

            var clientProvider = new EdFiApiClientProvider(new Lazy<EdFiApiClient>(ClientFactory));

            return (new EdFiApiSourceCapabilities(clientProvider, new SourceEdFiApiVersionMetadataProvider(clientProvider)), fake);
        }

        private static void SetupPartitionsProbe(IFakeHttpRequestHandler fake, Func<HttpResponseMessage> createResponse)
        {
            A.CallTo(() => fake.Get(A<string>.Ignored, A<HttpRequestMessage>.That.Matches(msg => msg.RequestUri.LocalPath == ProbePath)))
                .ReturnsLazily(createResponse);
        }

        private static void PartitionsProbeMustNotHaveHappened(IFakeHttpRequestHandler fake)
        {
            A.CallTo(() => fake.Get(A<string>.Ignored, A<HttpRequestMessage>.That.Matches(msg => msg.RequestUri.LocalPath == ProbePath)))
                .MustNotHaveHappened();
        }

        [Test]
        public async Task Version_7_3_with_a_successful_probe_should_support_cursor_paging()
        {
            var (capabilities, fake) = Create("7.3");
            HttpRequestMessage probeRequest = null;

            A.CallTo(() => fake.Get(A<string>.Ignored, A<HttpRequestMessage>.That.Matches(msg => msg.RequestUri.LocalPath == ProbePath)))
                .ReturnsLazily((string _, HttpRequestMessage request) =>
                {
                    probeRequest = request;
                    return FakeResponse.OK(new { pageTokens = new[] { "abc" } });
                });

            (await capabilities.SupportsCursorPagingAsync("/ed-fi/students")).ShouldBeTrue();

            probeRequest.ShouldNotBeNull();
            probeRequest.RequestUri.ParseQueryString()["number"].ShouldBe("1");
        }

        [Test]
        public async Task Version_below_7_3_should_not_support_cursor_paging_and_should_not_probe()
        {
            var (capabilities, fake) = Create("7.2");
            SetupPartitionsProbe(fake, () => FakeResponse.OK(new { pageTokens = new[] { "abc" } }));

            (await capabilities.SupportsCursorPagingAsync("/ed-fi/students")).ShouldBeFalse();

            PartitionsProbeMustNotHaveHappened(fake);
        }

        [Test]
        public async Task Failed_probe_should_not_support_cursor_paging_and_should_warn_once()
        {
            TestHelpers.InitializeLogging();
            var (capabilities, fake) = Create("7.3");
            SetupPartitionsProbe(fake, FakeResponse.NotFound);

            using (TestCorrelator.CreateContext())
            {
                (await capabilities.SupportsCursorPagingAsync("/ed-fi/students")).ShouldBeFalse();

                TestCorrelator.GetLogEventsFromCurrentContext()
                    .Count(e => e.Level == LogEventLevel.Warning && e.MessageTemplate.Text.Contains("cursor paging"))
                    .ShouldBe(1);
            }
        }

        [Test]
        public async Task Probe_response_without_a_pageTokens_array_should_not_support_cursor_paging()
        {
            var (capabilities, fake) = Create("7.3");
            SetupPartitionsProbe(fake, () => FakeResponse.OK("{}"));

            (await capabilities.SupportsCursorPagingAsync("/ed-fi/students")).ShouldBeFalse();
        }

        [Test]
        public async Task Version_metadata_failure_should_not_support_cursor_paging()
        {
            var (capabilities, fake) = Create("7.3");

            A.CallTo(() => fake.Get($"{fake.BaseUrl}/", A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));

            (await capabilities.SupportsCursorPagingAsync("/ed-fi/students")).ShouldBeFalse();
        }

        [Test]
        public async Task Detection_should_run_once_per_run()
        {
            var (capabilities, fake) = Create("7.3");
            int probes = 0;

            SetupPartitionsProbe(fake, () =>
            {
                probes++;
                return FakeResponse.OK(new { pageTokens = new[] { "abc" } });
            });

            (await capabilities.SupportsCursorPagingAsync("/ed-fi/students")).ShouldBeTrue();
            (await capabilities.SupportsCursorPagingAsync("/ed-fi/schools")).ShouldBeTrue();

            probes.ShouldBe(1);
        }
    }
}
