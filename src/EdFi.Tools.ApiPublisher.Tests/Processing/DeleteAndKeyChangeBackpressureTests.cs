// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Blocks;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Messages;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing.Blocks;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using Newtonsoft.Json.Linq;
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
    /// Verifies the APIPUB-112 bounded-capacity behavior for the delete and key-change pipelines (each a
    /// chain of two bounded item blocks): with the target stalled, a bounded pipeline declines offers at
    /// capacity and recovers after draining, an unbounded (-1) pipeline accepts everything, and in both
    /// modes every accepted message is processed exactly once (no loss, no deadlock).
    /// </summary>
    [TestFixture]
    public class DeleteAndKeyChangeBackpressureTests
    {
        private const string Students = "/ed-fi/students";

        [TestCase(4, true)]
        [TestCase(-1, false)]
        public async Task Delete_blocks_should_decline_at_capacity_only_when_bounded_and_process_all_accepted_messages(
            int configuredCapacity,
            bool expectDecline)
        {
            TestHelpers.InitializeLogging();

            var gate = new ManualResetEventSlim(false);
            var getStarted = new SemaphoreSlim(0);
            int deletesCompleted = 0;

            try
            {
                var fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();

                // Stage 1 (GET by key) parks on the gate; stage 2 (DELETE) counts completions
                A.CallTo(
                        () => fakeTargetRequestHandler.Get(
                            A<string>.Ignored,
                            A<HttpRequestMessage>.That.Matches(msg => msg.RequestUri.LocalPath == $"/data/v3{Students}")))
                    .ReturnsLazily(
                        _ =>
                        {
                            getStarted.Release();
                            gate.Wait(TimeSpan.FromSeconds(30));

                            return new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StringContent(
                                    $@"[{{""id"":""{Guid.NewGuid():n}""}}]", Encoding.UTF8, "application/json")
                            };
                        });

                A.CallTo(() => fakeTargetRequestHandler.Delete(A<string>.Ignored, A<HttpRequestMessage>.Ignored))
                    .ReturnsLazily(
                        _ =>
                        {
                            Interlocked.Increment(ref deletesCompleted);

                            return new HttpResponseMessage(HttpStatusCode.NoContent);
                        });

                var options = TestHelpers.GetOptions();
                options.MaxDegreeOfParallelismForPostResourceItem = 1;
                options.ProcessingBlockBoundedCapacity = configuredCapacity;

                var factory = new DeleteResourceProcessingBlocksFactory(CreateTargetApiClientProvider(fakeTargetRequestHandler));

                var (inputBlock, outputBlock) = factory.CreateProcessingBlocks(CreateBlocksRequest(options));
                outputBlock.LinkTo(DataflowBlock.NullTarget<ErrorItemMessage>());

                int accepted = await RunSaturationScenarioAsync(
                    inputBlock,
                    CreateGetItemForDeletionMessage,
                    getStarted,
                    gate,
                    expectDecline);

                inputBlock.Complete();
                await outputBlock.Completion.WaitAsync(TimeSpan.FromSeconds(30));

                // Every accepted message must be processed exactly once (no loss, no duplication)
                deletesCompleted.ShouldBe(accepted);
            }
            finally
            {
                gate.Set();
            }
        }

        [TestCase(4, true)]
        [TestCase(-1, false)]
        public async Task Key_change_blocks_should_decline_at_capacity_only_when_bounded_and_process_all_accepted_messages(
            int configuredCapacity,
            bool expectDecline)
        {
            TestHelpers.InitializeLogging();

            var gate = new ManualResetEventSlim(false);
            var getStarted = new SemaphoreSlim(0);
            int putsCompleted = 0;

            try
            {
                var fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();

                // Stage 1 (GET by key) parks on the gate; stage 2 (PUT) counts completions
                A.CallTo(
                        () => fakeTargetRequestHandler.Get(
                            A<string>.Ignored,
                            A<HttpRequestMessage>.That.Matches(msg => msg.RequestUri.LocalPath == $"/data/v3{Students}")))
                    .ReturnsLazily(
                        _ =>
                        {
                            getStarted.Release();
                            gate.Wait(TimeSpan.FromSeconds(30));

                            return new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StringContent(
                                    $@"[{{""id"":""{Guid.NewGuid():n}"",""studentUniqueId"":""old""}}]",
                                    Encoding.UTF8,
                                    "application/json")
                            };
                        });

                A.CallTo(() => fakeTargetRequestHandler.Put(A<string>.Ignored, A<HttpRequestMessage>.Ignored))
                    .ReturnsLazily(
                        _ =>
                        {
                            Interlocked.Increment(ref putsCompleted);

                            return new HttpResponseMessage(HttpStatusCode.NoContent);
                        });

                var options = TestHelpers.GetOptions();
                options.MaxDegreeOfParallelismForPostResourceItem = 1;
                options.ProcessingBlockBoundedCapacity = configuredCapacity;

                var factory = new ChangeResourceKeyProcessingBlocksFactory(CreateTargetApiClientProvider(fakeTargetRequestHandler));

                var (inputBlock, outputBlock) = factory.CreateProcessingBlocks(CreateBlocksRequest(options));
                outputBlock.LinkTo(DataflowBlock.NullTarget<ErrorItemMessage>());

                int accepted = await RunSaturationScenarioAsync(
                    inputBlock,
                    CreateGetItemForKeyChangeMessage,
                    getStarted,
                    gate,
                    expectDecline);

                inputBlock.Complete();
                await outputBlock.Completion.WaitAsync(TimeSpan.FromSeconds(30));

                // Every accepted message must be processed exactly once (no loss, no duplication)
                putsCompleted.ShouldBe(accepted);
            }
            finally
            {
                gate.Set();
            }
        }

        /// <summary>
        /// Fills the pipeline to its (bounded) capacity, verifies decline behavior with the consumer
        /// stalled, then releases the consumer and verifies capacity is handed back. Returns the number
        /// of accepted messages. Mirrors BackpressureTests' POST-block scenario.
        /// </summary>
        private static async Task<int> RunSaturationScenarioAsync<TMessage>(
            ITargetBlock<TMessage> inputBlock,
            Func<TMessage> createMessage,
            SemaphoreSlim consumerStarted,
            ManualResetEventSlim gate,
            bool expectDecline)
        {
            const int InitialItems = 4;
            const int ExtraOffers = 12;

            int accepted = 0;

            for (int i = 0; i < InitialItems; i++)
            {
                var sendTask = inputBlock.SendAsync(createMessage());
                (await Task.WhenAny(sendTask, Task.Delay(TimeSpan.FromSeconds(30)))).ShouldBe(sendTask);
                sendTask.Result.ShouldBeTrue();
                accepted++;
            }

            (await consumerStarted.WaitAsync(TimeSpan.FromSeconds(30))).ShouldBeTrue();

            // With the consumer stalled and the buffer full, a bounded block must decline further offers,
            // while an unbounded block (-1, the rollback configuration) must accept all of them
            bool declined = false;

            for (int i = 0; i < ExtraOffers; i++)
            {
                if (inputBlock.Post(createMessage()))
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

            var extraSendTask = inputBlock.SendAsync(createMessage());
            (await Task.WhenAny(extraSendTask, Task.Delay(TimeSpan.FromSeconds(30)))).ShouldBe(extraSendTask);
            extraSendTask.Result.ShouldBeTrue();
            accepted++;

            return accepted;
        }

        private static GetItemForDeletionMessage CreateGetItemForDeletionMessage()
        {
            return new GetItemForDeletionMessage
            {
                ResourceUrl = Students,
                KeyValues = new JObject { ["studentUniqueId"] = Guid.NewGuid().ToString("n") },
                Id = Guid.NewGuid().ToString("n"),
                CancellationToken = CancellationToken.None,
            };
        }

        private static GetItemForKeyChangeMessage CreateGetItemForKeyChangeMessage()
        {
            return new GetItemForKeyChangeMessage
            {
                ResourceUrl = Students,
                ExistingKeyValues = new JObject { ["studentUniqueId"] = "old" },
                NewKeyValues = new JObject { ["studentUniqueId"] = Guid.NewGuid().ToString("n") },
                SourceId = Guid.NewGuid().ToString("n"),
                CancellationToken = CancellationToken.None,
            };
        }

        private static EdFiApiClientProvider CreateTargetApiClientProvider(IFakeHttpRequestHandler fakeTargetRequestHandler)
        {
            EdFiApiClient TargetApiClientFactory() =>
                new EdFiApiClient(
                    "TestTarget",
                    TestHelpers.GetTargetApiConnectionDetails(),
                    bearerTokenRefreshMinutes: 27,
                    ignoreSslErrors: true,
                    httpClientHandler: new HttpClientHandlerFakeBridge(fakeTargetRequestHandler));

            return new EdFiApiClientProvider(new Lazy<EdFiApiClient>(TargetApiClientFactory));
        }

        private static CreateBlocksRequest CreateBlocksRequest(Options options)
        {
            return new CreateBlocksRequest(
                options,
                Array.Empty<AuthorizationFailureHandling>(),
                new BufferBlock<ErrorItemMessage>(),
                javaScriptModuleFactory: null);
        }
    }
}
