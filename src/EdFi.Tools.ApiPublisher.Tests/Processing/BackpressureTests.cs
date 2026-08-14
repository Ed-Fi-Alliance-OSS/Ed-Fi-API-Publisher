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
using EdFi.Tools.ApiPublisher.Core.Processing.Blocks;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using EdFi.Tools.ApiPublisher.Tests.Extensions;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using Jering.Javascript.NodeJS;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Shouldly;
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// Reproduces the unbounded TPL Dataflow buffering behavior reported in APIPUB-112 and verifies that
    /// the processing blocks exert backpressure on the source once their bounded capacity is reached.
    /// </summary>
    [TestFixture]
    public class BackpressureTests
    {
        private const string Students = "/ed-fi/students";
        private const string StateEducationAgencies = "/ed-fi/stateEducationAgencies";

        [Test]
        public async Task Bounded_post_resource_block_should_decline_new_items_at_capacity_and_recover_after_draining()
        {
            TestHelpers.InitializeLogging();

            const int BoundedCapacity = 4;

            var gate = new ManualResetEventSlim(false);
            var postStarted = new SemaphoreSlim(0);
            int postsCompleted = 0;

            try
            {
                var fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();

                A.CallTo(
                        () => fakeTargetRequestHandler.Post(
                            A<string>.That.Matches(url => url.EndsWith(Students)),
                            A<HttpRequestMessage>.Ignored))
                    .ReturnsLazily(
                        _ =>
                        {
                            postStarted.Release();
                            gate.Wait(TimeSpan.FromSeconds(30));
                            Interlocked.Increment(ref postsCompleted);

                            return new HttpResponseMessage(HttpStatusCode.OK);
                        });

                var options = TestHelpers.GetOptions();
                options.MaxDegreeOfParallelismForPostResourceItem = 1;
                options.ProcessingBlockBoundedCapacity = BoundedCapacity;

                var (inputBlock, outputBlock) = CreatePostResourceBlocks(fakeTargetRequestHandler, options);
                outputBlock.LinkTo(DataflowBlock.NullTarget<ErrorItemMessage>());

                // Fill the block to its capacity (all sends must be accepted; the first item is dequeued for
                // processing and parks on the gate inside the fake POST handler)
                int accepted = 0;

                for (int i = 0; i < BoundedCapacity; i++)
                {
                    var sendTask = inputBlock.SendAsync(CreatePostItemMessage());
                    (await Task.WhenAny(sendTask, Task.Delay(TimeSpan.FromSeconds(30)))).ShouldBe(sendTask);
                    sendTask.Result.ShouldBeTrue();
                    accepted++;
                }

                (await postStarted.WaitAsync(TimeSpan.FromSeconds(30))).ShouldBeTrue();

                // With the consumer stalled and the buffer full, the block must start declining offered items.
                // (An unbounded block accepts everything here, which is the memory growth behavior of APIPUB-112.)
                bool declined = false;

                for (int i = 0; i < 3 && !declined; i++)
                {
                    if (inputBlock.Post(CreatePostItemMessage()))
                    {
                        accepted++;
                    }
                    else
                    {
                        declined = true;
                    }
                }

                declined.ShouldBeTrue();

                // Release the consumer; the block must hand capacity back and accept new items again
                gate.Set();

                var extraSendTask = inputBlock.SendAsync(CreatePostItemMessage());
                (await Task.WhenAny(extraSendTask, Task.Delay(TimeSpan.FromSeconds(30)))).ShouldBe(extraSendTask);
                extraSendTask.Result.ShouldBeTrue();
                accepted++;

                // Every accepted message must be processed (backpressure must never lose data)
                inputBlock.Complete();
                await outputBlock.Completion.WaitAsync(TimeSpan.FromSeconds(30));

                postsCompleted.ShouldBe(accepted);
            }
            finally
            {
                gate.Set();
            }
        }

        [Test]
        public async Task Retry_pipeline_blocks_should_remain_unbounded_so_deferred_authorization_retries_are_never_dropped()
        {
            TestHelpers.InitializeLogging();

            const int ItemCount = 10;

            var gate = new ManualResetEventSlim(false);
            int postsCompleted = 0;

            try
            {
                var fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();

                A.CallTo(
                        () => fakeTargetRequestHandler.Post(
                            A<string>.That.Matches(url => url.EndsWith(Students)),
                            A<HttpRequestMessage>.Ignored))
                    .ReturnsLazily(
                        _ =>
                        {
                            gate.Wait(TimeSpan.FromSeconds(30));
                            Interlocked.Increment(ref postsCompleted);

                            return new HttpResponseMessage(HttpStatusCode.OK);
                        });

                var options = TestHelpers.GetOptions();
                options.MaxDegreeOfParallelismForPostResourceItem = 1;

                // Deliberately tiny capacity -- the retry pipeline must ignore it, because messages arrive
                // via a synchronous Post from the main pipeline that would silently drop them when declined
                options.ProcessingBlockBoundedCapacity = 1;

                var (inputBlock, outputBlock) =
                    CreatePostResourceBlocks(fakeTargetRequestHandler, options, isRetryPipeline: true);

                outputBlock.LinkTo(DataflowBlock.NullTarget<ErrorItemMessage>());

                // With the consumer stalled, every Post must still be accepted immediately
                for (int i = 0; i < ItemCount; i++)
                {
                    inputBlock.Post(CreatePostItemMessage()).ShouldBeTrue();
                }

                gate.Set();

                inputBlock.Complete();
                await outputBlock.Completion.WaitAsync(TimeSpan.FromSeconds(30));

                postsCompleted.ShouldBe(ItemCount);
            }
            finally
            {
                gate.Set();
            }
        }

        [Test]
        public async Task When_target_posts_stall_source_page_fetching_should_halt_instead_of_buffering_all_pages()
        {
            TestHelpers.InitializeLogging();

            const int PageSize = 25;
            const int TotalItems = 1000;
            const int TotalPages = TotalItems / PageSize;

            var gate = new ManualResetEventSlim(false);
            var postStarted = new SemaphoreSlim(0);
            int pageGets = 0;
            int postsCompleted = 0;

            try
            {
                // -----------------------------------------------------------------
                //                      Source Requests
                // -----------------------------------------------------------------
                var suppliedPageOfResources = TestHelpers.GetGenericResourceFaker().Generate(PageSize);

                var fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler()
                    .AvailableChangeVersions(1100)
                    .ResourceCount(responseTotalCountHeader: TotalItems);

                A.CallTo(
                        () => fakeSourceRequestHandler.Get(
                            A<string>.Ignored,
                            A<HttpRequestMessage>.That.Matches(
                                msg => msg.RequestUri.LocalPath.EndsWith(StateEducationAgencies)
                                    && msg.RequestUri.ParseQueryString()["totalCount"] != "true")))
                    .ReturnsLazily(
                        call =>
                        {
                            Interlocked.Increment(ref pageGets);

                            var request = (HttpRequestMessage)call.Arguments[1];
                            long offset = long.Parse(request.RequestUri.ParseQueryString()["offset"] ?? "0");

                            // Pages beyond the advertised total are empty (ends the final-page continuation probe)
                            return offset < TotalItems
                                ? FakeResponse.OK(suppliedPageOfResources)
                                : FakeResponse.OK("[]");
                        });

                // -----------------------------------------------------------------
                //                      Target Requests
                // -----------------------------------------------------------------
                var fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();

                A.CallTo(
                        () => fakeTargetRequestHandler.Post(
                            A<string>.That.Matches(url => url.EndsWith(StateEducationAgencies)),
                            A<HttpRequestMessage>.Ignored))
                    .ReturnsLazily(
                        _ =>
                        {
                            postStarted.Release();
                            gate.Wait(TimeSpan.FromSeconds(60));
                            Interlocked.Increment(ref postsCompleted);

                            return new HttpResponseMessage(HttpStatusCode.OK);
                        });

                // -----------------------------------------------------------------
                //                  Source/Target Connection Details
                // -----------------------------------------------------------------
                var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(
                    include: new[] { StateEducationAgencies });

                var targetApiConnectionDetails = TestHelpers.GetTargetApiConnectionDetails();

                // -----------------------------------------------------------------
                //                    Options and Configuration
                // -----------------------------------------------------------------
                var options = TestHelpers.GetOptions();
                options.IncludeDescriptors = false;
                options.StreamingPageSize = PageSize;
                options.MaxDegreeOfParallelismForResourceProcessing = 1;
                options.MaxDegreeOfParallelismForPostResourceItem = 1;
                options.MaxDegreeOfParallelismForStreamResourcePages = 1;
                options.ProcessingBlockBoundedCapacity = PageSize;

                var changeProcessorConfiguration = TestHelpers.CreateChangeProcessorConfiguration(options);

                var changeProcessor = TestHelpers.CreateChangeProcessorWithDefaultDependencies(
                    options,
                    sourceApiConnectionDetails,
                    fakeSourceRequestHandler,
                    targetApiConnectionDetails,
                    fakeTargetRequestHandler);

                // -----------------------------------------------------------------
                //                              Act
                // -----------------------------------------------------------------
                // Task.Run is required here: the fake HTTP handlers complete synchronously, so calling
                // ProcessChangesAsync directly would run the entire (blocking) publish loop on this thread
                // and never reach the gate release below.
                var processingTask = Task.Run(
                    () => changeProcessor.ProcessChangesAsync(changeProcessorConfiguration, CancellationToken.None));

                // Wait until the (gated) target has started processing, then let page fetching settle
                (await postStarted.WaitAsync(TimeSpan.FromSeconds(30))).ShouldBeTrue();

                int pageGetsSnapshot = await GetStableValueAsync(() => Volatile.Read(ref pageGets));

                // Release the target and drain the pipeline to completion
                gate.Set();
                await processingTask.WaitAsync(TimeSpan.FromMinutes(2));

                // With backpressure, only a handful of pages fit in the bounded buffers while the target is
                // stalled. Without it (APIPUB-112), every page is fetched and buffered in memory.
                pageGetsSnapshot.ShouldBeLessThan(TotalPages / 4);

                // All items must still be published exactly once the target recovers (no loss, no deadlock)
                postsCompleted.ShouldBe(TotalItems);
            }
            finally
            {
                gate.Set();
            }
        }

        /// <summary>
        /// Samples a value until it stops changing for three consecutive 100ms intervals (or 10 seconds elapse),
        /// so assertions observe the pipeline's steady state rather than a timing-sensitive snapshot.
        /// </summary>
        private static async Task<int> GetStableValueAsync(Func<int> getValue)
        {
            int lastValue = getValue();
            int stableIntervals = 0;

            for (int i = 0; i < 100 && stableIntervals < 3; i++)
            {
                await Task.Delay(100);

                int currentValue = getValue();
                stableIntervals = currentValue == lastValue ? stableIntervals + 1 : 0;
                lastValue = currentValue;
            }

            return lastValue;
        }

        private static PostItemMessage CreatePostItemMessage(string resourceUrl = Students)
        {
            return new PostItemMessage
            {
                ResourceUrl = resourceUrl,
                Item = new JObject
                {
                    ["id"] = Guid.NewGuid().ToString("n"),
                    ["someProperty"] = "someValue",
                },
            };
        }

        private static (ITargetBlock<PostItemMessage> inputBlock, ISourceBlock<ErrorItemMessage> outputBlock)
            CreatePostResourceBlocks(
                IFakeHttpRequestHandler fakeTargetRequestHandler,
                Options options,
                bool isRetryPipeline = false)
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
                javaScriptModuleFactory: null)
            {
                IsRetryPipeline = isRetryPipeline,
            };

            return factory.CreateProcessingBlocks(createBlocksRequest);
        }
    }
}
