// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Connections.Api.DependencyResolution;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Blocks;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Messages;
using EdFi.Tools.ApiPublisher.Core.Capabilities;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Blocks;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using Jering.Javascript.NodeJS;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Shouldly;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// Verifies the error-path memory behavior for APIPUB-112: published errors must not retain parsed
    /// JObject graphs, and the error publishing pipeline must be bounded so an error storm (e.g. sustained
    /// authorization failures) cannot queue errors in memory without limit.
    /// </summary>
    [TestFixture]
    public class ErrorPathMemoryTests
    {
        private const string Students = "/ed-fi/students";

        [Test]
        public async Task Failed_post_error_should_carry_compact_serialized_body_instead_of_parsed_item()
        {
            TestHelpers.InitializeLogging();

            var fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();

            // BadRequest is a permanent (non-transient) failure, so a single attempt produces a final error
            A.CallTo(
                    () => fakeTargetRequestHandler.Post(
                        A<string>.That.Matches(url => url.EndsWith(Students)),
                        A<HttpRequestMessage>.Ignored))
                .Returns(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"message\":\"Validation failed.\"}")
                });

            var options = TestHelpers.GetOptions();
            options.MaxDegreeOfParallelismForPostResourceItem = 1;

            var (inputBlock, outputBlock) = CreatePostResourceBlocks(fakeTargetRequestHandler, options);

            var originalItem = new JObject
            {
                ["id"] = Guid.NewGuid().ToString("n"),
                ["someProperty"] = "someValue",
            };

            var postItemMessage = new PostItemMessage
            {
                ResourceUrl = Students,
                Item = originalItem,
            };

            inputBlock.Post(postItemMessage).ShouldBeTrue();
            inputBlock.Complete();

            var error = await outputBlock.ReceiveAsync(TimeSpan.FromSeconds(30));
            await outputBlock.Completion.WaitAsync(TimeSpan.FromSeconds(30));

            // The body must be retained as a single raw JSON string, not a live token graph
            error.Body.ShouldBeOfType<JRaw>();
            error.Body.ShouldNotBeSameAs(originalItem);

            // The raw JSON must still round-trip to the failed item's content (id/_etag are stripped before POST)
            var roundTripped = JObject.Parse(((JRaw)error.Body).Value!.ToString()!);
            roundTripped["someProperty"]!.Value<string>().ShouldBe("someValue");

            // The published (serialized) error must still include the body content for diagnostics
            string serializedError = JsonConvert.SerializeObject(error, Formatting.Indented);
            serializedError.ShouldContain("someValue");
        }

        [Test]
        public async Task Error_ingestion_should_be_bounded_by_default_and_deliver_delayed_errors_without_loss()
        {
            TestHelpers.InitializeLogging();

            var gate = new ManualResetEventSlim(false);
            int errorsPublished = 0;

            try
            {
                var errorPublisher = A.Fake<IErrorPublisher>();

                A.CallTo(() => errorPublisher.PublishErrorsAsync(A<ErrorItemMessage[]>.Ignored))
                    .ReturnsLazily(
                        call => Task.Run(
                            () =>
                            {
                                gate.Wait(TimeSpan.FromSeconds(30));
                                Interlocked.Add(ref errorsPublished, ((ErrorItemMessage[])call.Arguments[0]!).Length);
                            }));

                var options = TestHelpers.GetOptions();
                options.ErrorPublishingBatchSize = 2;

                var (ingestionBlock, completionBlock) = new PublishErrorsBlocksFactory(errorPublisher).CreateBlocks(options);

                // With the publisher stalled, a bounded ingestion path must start declining synchronous posts
                // after a small finite number of errors (an unbounded path accepts all of them -- APIPUB-112)
                int accepted = 0;
                bool declined = false;

                for (int i = 0; i < 100; i++)
                {
                    // Brief settle so batches migrate downstream and capacity is genuinely exhausted
                    if (!ingestionBlock.Post(CreateErrorItemMessage()) && !await RetryPostWithSettleAsync(ingestionBlock))
                    {
                        declined = true;

                        break;
                    }

                    accepted++;
                }

                declined.ShouldBeTrue();
                accepted.ShouldBeLessThan(100);

                // SendAsync (the produce path) must wait for capacity rather than drop the error
                var delayedSend = ingestionBlock.SendAsync(CreateErrorItemMessage());

                gate.Set();

                (await delayedSend.WaitAsync(TimeSpan.FromSeconds(30))).ShouldBeTrue();

                ingestionBlock.Complete();
                await completionBlock.Completion.WaitAsync(TimeSpan.FromSeconds(30));

                // Every accepted error must be published exactly once (bounding must never lose accepted errors)
                errorsPublished.ShouldBe(accepted + 1);
            }
            finally
            {
                gate.Set();
            }
        }

        [Test]
        public async Task Faulted_error_publisher_should_fault_ingestion_so_parked_producers_complete_instead_of_hanging()
        {
            TestHelpers.InitializeLogging();

            var errorPublisher = A.Fake<IErrorPublisher>();

            A.CallTo(() => errorPublisher.PublishErrorsAsync(A<ErrorItemMessage[]>.Ignored))
                .ThrowsAsync(new InvalidOperationException("Error store is unavailable."));

            var options = TestHelpers.GetOptions();
            options.ErrorPublishingBatchSize = 2;

            var (ingestionBlock, completionBlock) = new PublishErrorsBlocksFactory(errorPublisher).CreateBlocks(options);

            // Park producers well past the bound (capacity is 2 x batch size = 4); the first published batch
            // faults the completion block, which severs the link and leaves the bound permanently full
            var sends = Enumerable.Range(0, 20)
                .Select(_ => ingestionBlock.SendAsync(CreateErrorItemMessage()))
                .ToArray();

            // The completion block must fault with the publisher's exception (this already worked pre-fix)
            await Should.ThrowAsync<InvalidOperationException>(
                completionBlock.Completion.WaitAsync(TimeSpan.FromSeconds(10)));

            // The fault must propagate back to the ingestion block so parked SendAsync producers complete
            // (with false) instead of waiting forever for capacity that can never free up
            await Task.WhenAll(sends).WaitAsync(TimeSpan.FromSeconds(10));

            sends[^1].Result.ShouldBeFalse();
        }

        [Test]
        public async Task Error_ingestion_should_be_unbounded_when_bounding_is_explicitly_disabled()
        {
            TestHelpers.InitializeLogging();

            const int ErrorCount = 500;

            var gate = new ManualResetEventSlim(false);
            int errorsPublished = 0;

            try
            {
                var errorPublisher = A.Fake<IErrorPublisher>();

                A.CallTo(() => errorPublisher.PublishErrorsAsync(A<ErrorItemMessage[]>.Ignored))
                    .ReturnsLazily(
                        call => Task.Run(
                            () =>
                            {
                                gate.Wait(TimeSpan.FromSeconds(30));
                                Interlocked.Add(ref errorsPublished, ((ErrorItemMessage[])call.Arguments[0]!).Length);
                            }));

                var options = TestHelpers.GetOptions();
                options.ErrorPublishingBatchSize = 2;
                options.ProcessingBlockBoundedCapacity = -1;

                var (ingestionBlock, completionBlock) = new PublishErrorsBlocksFactory(errorPublisher).CreateBlocks(options);

                // With bounding disabled, every synchronous post must be accepted even with the publisher stalled
                for (int i = 0; i < ErrorCount; i++)
                {
                    ingestionBlock.Post(CreateErrorItemMessage()).ShouldBeTrue();
                }

                gate.Set();

                ingestionBlock.Complete();
                await completionBlock.Completion.WaitAsync(TimeSpan.FromSeconds(30));

                errorsPublished.ShouldBe(ErrorCount);
            }
            finally
            {
                gate.Set();
            }
        }

        /// <summary>
        /// Retries a declined Post for up to one second, so a decline caused by a transient in-flight batch
        /// handoff isn't mistaken for capacity exhaustion.
        /// </summary>
        private static async Task<bool> RetryPostWithSettleAsync(ITargetBlock<ErrorItemMessage> ingestionBlock)
        {
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(100);

                if (ingestionBlock.Post(CreateErrorItemMessage()))
                {
                    return true;
                }
            }

            return false;
        }

        private static ErrorItemMessage CreateErrorItemMessage()
        {
            return new ErrorItemMessage
            {
                Method = HttpMethod.Post.ToString(),
                ResourceUrl = Students,
                Id = Guid.NewGuid().ToString("n"),
                Body = new JRaw("{\"someProperty\":\"someValue\"}"),
                ResponseStatus = HttpStatusCode.Forbidden,
                ResponseContent = "{\"message\":\"Authorization denied.\"}",
            };
        }

        private static (ITargetBlock<PostItemMessage> inputBlock, ISourceBlock<ErrorItemMessage> outputBlock)
            CreatePostResourceBlocks(IFakeHttpRequestHandler fakeTargetRequestHandler, Options options)
        {
            EdFiApiClient TargetApiClientFactory() =>
                new EdFiApiClient(
                    "TestTarget",
                    TestHelpers.GetTargetApiConnectionDetails(),
                    bearerTokenRefreshMinutes: 27,
                    ignoreSslErrors: true,
                    httpClientHandler: new HttpClientHandlerFakeBridge(fakeTargetRequestHandler));

            var targetEdFiApiClientProvider = new EdFiApiClientProvider(new Lazy<EdFiApiClient>(TargetApiClientFactory));

            var factory = new PostResourceProcessingBlocksFactory(
                A.Fake<INodeJSService>(),
                targetEdFiApiClientProvider,
                TestHelpers.GetSourceApiConnectionDetails(),
                A.Fake<ISourceCapabilities>(),
                A.Fake<ISourceResourceItemProvider>());

            var createBlocksRequest = new CreateBlocksRequest(
                options,
                Array.Empty<AuthorizationFailureHandling>(),
                new BufferBlock<ErrorItemMessage>(),
                javaScriptModuleFactory: null);

            return factory.CreateProcessingBlocks(createBlocksRequest);
        }
    }
}
