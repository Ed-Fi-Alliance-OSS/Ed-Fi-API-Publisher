// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
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
using System.Net.Http;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// Verifies cursor paging detection (APIPUB-139): a single GET /{resource}/partitions?number=1 probe
    /// returning a 200 response with a pageTokens array. The probe is version-independent -- a source may
    /// have backported the feature onto an older ODS/API version -- and any failure mode (404, other
    /// unsuccessful status, missing pageTokens array, or a thrown exception) resolves to offset paging.
    /// The answer is determined once per run.
    /// </summary>
    [TestFixture]
    public class EdFiApiSourceCapabilitiesCursorPagingTests
    {
        private const string ProbePath = "/data/v3/ed-fi/students/partitions";

        private static (EdFiApiSourceCapabilities capabilities, IFakeHttpRequestHandler fake) Create()
        {
            var fake = TestHelpers.GetFakeBaselineSourceApiRequestHandler();

            EdFiApiClient ClientFactory() =>
                new EdFiApiClient(
                    "TestSource",
                    TestHelpers.GetSourceApiConnectionDetails(),
                    bearerTokenRefreshMinutes: 27,
                    ignoreSslErrors: true,
                    httpClientHandler: new HttpClientHandlerFakeBridge(fake));

            var clientProvider = new EdFiApiClientProvider(new Lazy<EdFiApiClient>(ClientFactory));

            return (new EdFiApiSourceCapabilities(clientProvider), fake);
        }

        private static void SetupPartitionsProbe(IFakeHttpRequestHandler fake, Func<HttpResponseMessage> createResponse)
        {
            A.CallTo(() => fake.Get(A<string>.Ignored, A<HttpRequestMessage>.That.Matches(msg => msg.RequestUri.LocalPath == ProbePath)))
                .ReturnsLazily(createResponse);
        }

        private static void PartitionsProbeMustHaveHappenedOnceExactly(IFakeHttpRequestHandler fake)
        {
            A.CallTo(() => fake.Get(A<string>.Ignored, A<HttpRequestMessage>.That.Matches(msg => msg.RequestUri.LocalPath == ProbePath)))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task Successful_probe_should_support_cursor_paging()
        {
            var (capabilities, fake) = Create();
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
        public async Task Source_without_partitions_endpoint_should_not_support_cursor_paging()
        {
            var (capabilities, fake) = Create();
            SetupPartitionsProbe(fake, FakeResponse.NotFound);

            (await capabilities.SupportsCursorPagingAsync("/ed-fi/students")).ShouldBeFalse();

            PartitionsProbeMustHaveHappenedOnceExactly(fake);
        }

        [Test]
        public async Task Source_with_backported_partitions_support_should_support_cursor_paging_regardless_of_version()
        {
            var fake = TestHelpers.GetFakeBaselineSourceApiRequestHandler().ApiVersionMetadata(apiVersion: "7.1");

            EdFiApiClient ClientFactory() =>
                new EdFiApiClient(
                    "TestSource",
                    TestHelpers.GetSourceApiConnectionDetails(),
                    bearerTokenRefreshMinutes: 27,
                    ignoreSslErrors: true,
                    httpClientHandler: new HttpClientHandlerFakeBridge(fake));

            var clientProvider = new EdFiApiClientProvider(new Lazy<EdFiApiClient>(ClientFactory));
            var capabilities = new EdFiApiSourceCapabilities(clientProvider);

            SetupPartitionsProbe(fake, () => FakeResponse.OK(new { pageTokens = new[] { "abc" } }));

            (await capabilities.SupportsCursorPagingAsync("/ed-fi/students")).ShouldBeTrue();
        }

        [Test]
        public async Task Failed_probe_should_not_support_cursor_paging_and_should_warn_once()
        {
            TestHelpers.InitializeLogging();
            var (capabilities, fake) = Create();
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
            var (capabilities, fake) = Create();
            SetupPartitionsProbe(fake, () => FakeResponse.OK("{}"));

            (await capabilities.SupportsCursorPagingAsync("/ed-fi/students")).ShouldBeFalse();
        }

        [Test]
        public async Task Detection_should_run_once_per_run()
        {
            var (capabilities, fake) = Create();
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
