// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Counting;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using NUnit.Framework;
using Shouldly;
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// Verifies the total-count provider's transient-retry behavior: with ResponseHeadersRead
    /// (see APIPUB-134) a failed response being retried holds a live connection until disposed,
    /// so the retry callback must dispose it.
    /// </summary>
    [TestFixture]
    public class SourceTotalCountRetryTests
    {
        [Test]
        public async Task Transient_retry_responses_should_be_disposed_and_the_count_still_obtained()
        {
            TestHelpers.InitializeLogging();

            var fakeRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler();

            var transientContent = new InstrumentedJsonContent(@"{""message"":""temporarily unavailable""}");
            int attempts = 0;

            A.CallTo(
                    () => fakeRequestHandler.Get(
                        A<string>.Ignored,
                        A<HttpRequestMessage>.That.Matches(msg => msg.RequestUri.LocalPath == "/data/v3/ed-fi/students")))
                .ReturnsLazily(
                    () =>
                    {
                        if (Interlocked.Increment(ref attempts) == 1)
                        {
                            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = transientContent };
                        }

                        var response = new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent("[]", Encoding.UTF8, "application/json")
                        };

                        response.Headers.TryAddWithoutValidation("Total-Count", "42");

                        return response;
                    });

            EdFiApiClient SourceApiClientFactory() =>
                new EdFiApiClient(
                    "TestSource",
                    TestHelpers.GetSourceApiConnectionDetails(),
                    bearerTokenRefreshMinutes: 27,
                    ignoreSslErrors: true,
                    httpClientHandler: new HttpClientHandlerFakeBridge(fakeRequestHandler));

            var sourceClientProvider = A.Fake<ISourceEdFiApiClientProvider>();
            A.CallTo(() => sourceClientProvider.GetApiClient()).Returns(SourceApiClientFactory());

            var provider = new EdFiApiSourceTotalCountProvider(sourceClientProvider);
            var errorBlock = new BufferBlock<ErrorItemMessage>();

            var (success, totalCount) = await provider.TryGetTotalCountAsync(
                "/ed-fi/students",
                TestHelpers.GetOptions(),
                changeWindow: null,
                errorBlock,
                CancellationToken.None);

            attempts.ShouldBe(2);
            success.ShouldBeTrue();
            totalCount.ShouldBe(42);
            errorBlock.Count.ShouldBe(0);

            // The transient failure being retried must have been disposed by the retry callback
            transientContent.ContentDisposed.ShouldBeTrue();
        }

        [Test]
        public async Task Exhausted_transient_retries_should_report_failure_publish_an_error_and_dispose_every_response()
        {
            TestHelpers.InitializeLogging();

            var fakeRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler();

            var contents = new System.Collections.Generic.List<InstrumentedJsonContent>();

            A.CallTo(
                    () => fakeRequestHandler.Get(
                        A<string>.Ignored,
                        A<HttpRequestMessage>.That.Matches(msg => msg.RequestUri.LocalPath == "/data/v3/ed-fi/students")))
                .ReturnsLazily(
                    () =>
                    {
                        var content = new InstrumentedJsonContent(@"{""message"":""temporarily unavailable""}");
                        contents.Add(content);

                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = content };
                    });

            EdFiApiClient SourceApiClientFactory() =>
                new EdFiApiClient(
                    "TestSource",
                    TestHelpers.GetSourceApiConnectionDetails(),
                    bearerTokenRefreshMinutes: 27,
                    ignoreSslErrors: true,
                    httpClientHandler: new HttpClientHandlerFakeBridge(fakeRequestHandler));

            var sourceClientProvider = A.Fake<ISourceEdFiApiClientProvider>();
            A.CallTo(() => sourceClientProvider.GetApiClient()).Returns(SourceApiClientFactory());

            var provider = new EdFiApiSourceTotalCountProvider(sourceClientProvider);
            var errorBlock = new BufferBlock<ErrorItemMessage>();

            // GetOptions configures MaxRetryAttempts = 2, so exhaustion means 3 attempts in total
            var (success, totalCount) = await provider.TryGetTotalCountAsync(
                "/ed-fi/students",
                TestHelpers.GetOptions(),
                changeWindow: null,
                errorBlock,
                CancellationToken.None);

            success.ShouldBeFalse();
            totalCount.ShouldBe(0);
            contents.Count.ShouldBe(3);

            // The final failure is published so overall processing is forced to fail
            errorBlock.Count.ShouldBe(1);

            // Every response was disposed: the retried ones by the retry callback, the final one explicitly
            contents.ShouldAllBe(c => c.ContentDisposed);
        }
    }
}
