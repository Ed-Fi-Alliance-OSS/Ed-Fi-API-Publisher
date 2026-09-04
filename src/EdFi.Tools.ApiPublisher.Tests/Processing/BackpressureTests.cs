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
using System.Collections.Concurrent;
using System.Linq;
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

        /// <summary>
        /// Pins the TransformManyBlock BoundedCapacity semantics that the page-streaming bound relies on,
        /// since they've been read two different ways in review: (a) expanded outputs DO count toward the
        /// bound (the block adjusts its bounding count by the output count after the delegate runs), but
        /// (b) input acceptance is gated in MESSAGE units -- each unprocessed input counts as 1 -- so a
        /// bound of N admits up to N input messages before any expansion, and every accepted input is
        /// still processed even once expansion has pushed the count far past the bound. Consequence: an
        /// item-denominated bound of N on a page-expanding block admits up to N whole pages (N x pageSize
        /// items), which is why the pages block's capacity is denominated in page messages instead.
        /// </summary>
        [Test]
        public async Task TransformManyBlock_bound_counts_expanded_outputs_but_gates_acceptance_in_message_units()
        {
            const int BoundedCapacity = 10;
            const int ExpansionFactor = 10;
            const int OfferedInputs = 100;

            using var expansionGate = new ManualResetEventSlim(false);
            int processedInputs = 0;

            var block = new TransformManyBlock<int, int>(
                i =>
                {
                    // Held closed until all offers land, so acceptance counting is deterministic (the pool
                    // thread cannot race ahead and expand the first input while later Posts are still arriving)
                    expansionGate.Wait(TimeSpan.FromSeconds(30));

                    Interlocked.Increment(ref processedInputs);

                    return Enumerable.Range(0, ExpansionFactor);
                },
                new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 1, BoundedCapacity = BoundedCapacity });

            // No consumer linked: outputs accumulate in the block's own buffer

            int accepted = 0;

            for (int i = 0; i < OfferedInputs; i++)
            {
                if (block.Post(i))
                {
                    accepted++;
                }
            }

            // (b) Acceptance is gated per input message: with no expansion having run yet, exactly
            // BoundedCapacity inputs are admitted.
            accepted.ShouldBe(BoundedCapacity);

            // (a) Release expansion, wait for the block to go idle, then verify every accepted input was
            // processed even though the very first expansion (10 outputs) already saturated the bound --
            // and that the expanded outputs now count against it, blocking any further acceptance.
            expansionGate.Set();
            await GetStableValueAsync(() => Volatile.Read(ref processedInputs));

            processedInputs.ShouldBe(accepted);
            block.OutputCount.ShouldBe(accepted * ExpansionFactor);
            block.OutputCount.ShouldBeGreaterThan(BoundedCapacity);
            block.Post(999).ShouldBeFalse();
        }

        [TestCase(4, true)]
        [TestCase(-1, false)]
        public async Task Post_resource_block_should_decline_at_capacity_only_when_bounded_and_recover_after_draining(
            int configuredCapacity,
            bool expectDecline)
        {
            TestHelpers.InitializeLogging();

            const int InitialItems = 4;
            const int ExtraOffers = 12;

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
                options.ProcessingBlockBoundedCapacity = configuredCapacity;

                var (inputBlock, outputBlock) = CreatePostResourceBlocks(fakeTargetRequestHandler, options);
                outputBlock.LinkTo(DataflowBlock.NullTarget<ErrorItemMessage>());

                // Fill the block up to the bounded capacity (all sends must be accepted in both modes; the
                // first item is dequeued for processing and parks on the gate inside the fake POST handler)
                int accepted = 0;

                for (int i = 0; i < InitialItems; i++)
                {
                    var sendTask = inputBlock.SendAsync(CreatePostItemMessage());
                    (await Task.WhenAny(sendTask, Task.Delay(TimeSpan.FromSeconds(30)))).ShouldBe(sendTask);
                    sendTask.Result.ShouldBeTrue();
                    accepted++;
                }

                (await postStarted.WaitAsync(TimeSpan.FromSeconds(30))).ShouldBeTrue();

                // With the consumer stalled and the buffer full, a bounded block must decline every further
                // offer, while an unbounded block (-1, the rollback configuration) must accept all of them --
                // which is the memory growth behavior of APIPUB-112.
                bool declined = false;

                for (int i = 0; i < ExtraOffers; i++)
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

                declined.ShouldBe(expectDecline);

                if (!expectDecline)
                {
                    accepted.ShouldBe(InitialItems + ExtraOffers);
                }

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

        [TestCase(25, true)]
        [TestCase(-1, false)]
        public async Task When_target_posts_stall_source_page_fetching_should_halt_only_when_bounded(
            int configuredCapacity,
            bool expectBackpressure)
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
                var resourceFaker = TestHelpers.GetGenericResourceFaker();

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
                            if (offset >= TotalItems)
                            {
                                return FakeResponse.OK("[]");
                            }

                            // Stamp a unique, position-derived marker into each item (it survives the POST,
                            // unlike "id") so the assertions below can prove every distinct source item was
                            // published exactly once -- not merely that the number of POSTs matched.
                            var page = resourceFaker.Generate(PageSize);

                            for (int i = 0; i < page.Count; i++)
                            {
                                page[i].VehicleManufacturer = $"item-{offset + i}";
                            }

                            return FakeResponse.OK(page);
                        });

                // -----------------------------------------------------------------
                //                      Target Requests
                // -----------------------------------------------------------------
                var postedStamps = new ConcurrentBag<string>();

                var fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();

                A.CallTo(
                        () => fakeTargetRequestHandler.Post(
                            A<string>.That.Matches(url => url.EndsWith(StateEducationAgencies)),
                            A<HttpRequestMessage>.Ignored))
                    .ReturnsLazily(
                        call =>
                        {
                            var request = (HttpRequestMessage)call.Arguments[1];
                            string body = request.Content!.ReadAsStringAsync().Result;
                            postedStamps.Add(JObject.Parse(body)["vehicleManufacturer"]!.Value<string>()!);

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
                options.ProcessingBlockBoundedCapacity = configuredCapacity;

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

                // With backpressure, only the pages that fit in the bounded buffers are fetched while the
                // target is stalled: the pages block holds max(2 x pagesDOP, itemCap/pageSize) = 2 page
                // messages plus one being expanded, and the POST block holds itemCap = 1 page of items --
                // roughly 4 pages, plus slack for handoffs. Without backpressure (-1, the APIPUB-112
                // behavior), every page is fetched and buffered.
                if (expectBackpressure)
                {
                    pageGetsSnapshot.ShouldBeLessThanOrEqualTo(6);
                }
                else
                {
                    pageGetsSnapshot.ShouldBeGreaterThanOrEqualTo(TotalPages);
                }

                // Every distinct source item must be published exactly once after the target recovers
                // (no loss, no duplication, no deadlock) in both modes
                postsCompleted.ShouldBe(TotalItems);
                postedStamps.Count.ShouldBe(TotalItems);

                postedStamps.ToHashSet()
                    .SetEquals(Enumerable.Range(0, TotalItems).Select(i => $"item-{i}"))
                    .ShouldBeTrue();
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
