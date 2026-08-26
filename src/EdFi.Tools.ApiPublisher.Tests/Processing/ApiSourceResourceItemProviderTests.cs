// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Connections.Api.DependencyResolution;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using NUnit.Framework;
using Shouldly;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// Verifies that the source item lookup used for missing-dependency resolution honors the caller's
    /// cancellation token -- both for the GET itself and for the retry delays between transient failures --
    /// instead of executing under <see cref="CancellationToken.None" /> (see PR #152 review).
    /// </summary>
    [TestFixture]
    public class ApiSourceResourceItemProviderTests
    {
        private const string MissingItemUrl = "/ed-fi/students/abc123";

        [Test]
        public void Should_stop_retrying_a_transient_failure_once_the_cancellation_token_is_cancelled()
        {
            TestHelpers.InitializeLogging();

            var fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler();

            // Every GET of the item is a transient failure, so the provider would keep retrying with delays
            A.CallTo(() => fakeSourceRequestHandler.Get(A<string>.That.EndsWith(MissingItemUrl), A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(
                    () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        Content = new StringContent("{ \"message\": \"unavailable\" }", Encoding.UTF8, "application/json")
                    });

            var options = TestHelpers.GetOptions();

            // A retry delay that would dwarf the test's own timeout if cancellation were ignored
            options.RetryStartingDelayMilliseconds = 60_000;
            options.MaxRetryAttempts = 2;

            EdFiApiClient SourceApiClientFactory() =>
                new EdFiApiClient(
                    "TestSource",
                    TestHelpers.GetSourceApiConnectionDetails(),
                    bearerTokenRefreshMinutes: 27,
                    ignoreSslErrors: true,
                    httpClientHandler: new HttpClientHandlerFakeBridge(fakeSourceRequestHandler));

            var provider = new ApiSourceResourceItemProvider(
                new EdFiApiClientProvider(new Lazy<EdFiApiClient>(SourceApiClientFactory)),
                options);

            using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

            var stopwatch = Stopwatch.StartNew();

            // A TimeoutException here (rather than a cancellation) means the token was not honored
            Assert.CatchAsync<OperationCanceledException>(
                (Func<Task>)(() => provider.TryGetResourceItemAsync(MissingItemUrl, cancellationSource.Token)
                    .WaitAsync(TimeSpan.FromSeconds(15))));

            stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15));

            // The first attempt was made, then the retry delay was abandoned on cancellation
            A.CallTo(() => fakeSourceRequestHandler.Get(A<string>.That.EndsWith(MissingItemUrl), A<HttpRequestMessage>.Ignored))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public async Task Should_return_the_item_when_the_source_responds_successfully()
        {
            TestHelpers.InitializeLogging();

            const string ItemJson = "{ \"id\": \"abc123\", \"studentUniqueId\": \"S-1\" }";

            var fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler();

            A.CallTo(() => fakeSourceRequestHandler.Get(A<string>.That.EndsWith(MissingItemUrl), A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(
                    () => new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(ItemJson, Encoding.UTF8, "application/json")
                    });

            EdFiApiClient SourceApiClientFactory() =>
                new EdFiApiClient(
                    "TestSource",
                    TestHelpers.GetSourceApiConnectionDetails(),
                    bearerTokenRefreshMinutes: 27,
                    ignoreSslErrors: true,
                    httpClientHandler: new HttpClientHandlerFakeBridge(fakeSourceRequestHandler));

            var provider = new ApiSourceResourceItemProvider(
                new EdFiApiClientProvider(new Lazy<EdFiApiClient>(SourceApiClientFactory)),
                TestHelpers.GetOptions());

            var (success, itemJson) = await provider.TryGetResourceItemAsync(MissingItemUrl, CancellationToken.None);

            success.ShouldBeTrue();
            itemJson.ShouldBe(ItemJson);
        }
    }
}
