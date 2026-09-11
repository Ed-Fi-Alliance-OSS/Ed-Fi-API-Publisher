// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Metadata;
using EdFi.Tools.ApiPublisher.Core.Processing.Blocks;
using EdFi.Tools.ApiPublisher.Core.Processing.Handlers;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using NUnit.Framework;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// Pins why the pages block is an ActionBlock feeding a bounded BufferBlock (APIPUB-139): Dataflow bounding gates
    /// input acceptance only, so a TransformManyBlock never pauses an in-progress expansion -- even with the
    /// IAsyncEnumerable overload -- and a page message that expands into a whole partition would be buffered in full.
    /// SendAsync into a bounded buffer does pause the handler, and therefore the page fetching behind it.
    /// </summary>
    [TestFixture]
    public class StreamResourcePagesBlockBackpressureTests
    {
        private const int ItemsPerMessage = 1000;

        /// <summary>A handler that yields ItemsPerMessage items per message, counting how many it has produced.</summary>
        private sealed class CountingPageHandler : IStreamResourcePageMessageHandler
        {
            private int _produced;

            public int Produced => Volatile.Read(ref _produced);

            public async IAsyncEnumerable<TProcessDataMessage> HandleStreamResourcePageAsync<TProcessDataMessage>(
                StreamResourcePageMessage<TProcessDataMessage> message,
                Options options,
                ITargetBlock<ErrorItemMessage> errorHandlingBlock)
            {
                for (int i = 0; i < ItemsPerMessage; i++)
                {
                    Interlocked.Increment(ref _produced);
                    await Task.Yield();
                    yield return default;
                }
            }
        }

        /// <summary>A handler whose sequence throws on the first pull, standing in for e.g. the authentication rethrow.</summary>
        private sealed class ThrowingPageHandler : IStreamResourcePageMessageHandler
        {
            public async IAsyncEnumerable<TProcessDataMessage> HandleStreamResourcePageAsync<TProcessDataMessage>(
                StreamResourcePageMessage<TProcessDataMessage> message,
                Options options,
                ITargetBlock<ErrorItemMessage> errorHandlingBlock)
            {
                await Task.Yield();

                throw new InvalidOperationException("The source API client can no longer authenticate.");

#pragma warning disable CS0162 // Unreachable code: an iterator needs a yield to be an IAsyncEnumerable
                yield break;
#pragma warning restore CS0162
            }
        }

        private static async IAsyncEnumerable<int> Produce(int count, Action onItem)
        {
            for (int i = 0; i < count; i++)
            {
                onItem();
                await Task.Yield();
                yield return i;
            }
        }

        [Test]
        public async Task TransformManyBlock_async_enumerable_overload_does_not_pause_an_in_progress_expansion()
        {
            int produced = 0;

            var block = new TransformManyBlock<int, int>(
                _ => Produce(ItemsPerMessage, () => Interlocked.Increment(ref produced)),
                new ExecutionDataflowBlockOptions { BoundedCapacity = 10, MaxDegreeOfParallelism = 1 });

            block.Post(1).ShouldBeTrue();
            await GetStableValueAsync(() => Volatile.Read(ref produced));

            // Nothing consumes the block, yet the whole expansion lands in its output buffer
            produced.ShouldBe(ItemsPerMessage);
            block.OutputCount.ShouldBe(ItemsPerMessage);
        }

        [Test]
        public async Task Pages_block_should_stop_pulling_page_items_when_the_item_buffer_is_full()
        {
            var handler = new CountingPageHandler();
            var options = TestHelpers.GetOptions();
            options.ProcessingBlockBoundedCapacity = 10;
            options.MaxDegreeOfParallelismForStreamResourcePages = 1;

            var block = new StreamResourcePagesBlockFactory(handler, A.Fake<IRunSummaryCollector>()).CreateBlock<object>(options, new BufferBlock<ErrorItemMessage>());

            block.Post(new StreamResourcePageMessage<object> { ResourceUrl = "/ed-fi/students", CancellationSource = new CancellationTokenSource() }).ShouldBeTrue();

            // No consumer linked yet: the handler must stall after filling the item buffer (+1 item parked in SendAsync)
            int stalledAt = await GetStableValueAsync(() => handler.Produced);
            stalledAt.ShouldBeInRange(10, 11);

            // Drain: every item is delivered and completion flows through
            int consumed = 0;
            var sink = new ActionBlock<object>(_ => Interlocked.Increment(ref consumed));
            block.LinkTo(sink, new DataflowLinkOptions { PropagateCompletion = true });
            block.Complete();

            await sink.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            consumed.ShouldBe(ItemsPerMessage);
            handler.Produced.ShouldBe(ItemsPerMessage);
        }

        [Test]
        public async Task Cancelling_the_resource_should_release_a_handler_parked_on_the_full_item_buffer()
        {
            var handler = new CountingPageHandler();
            var options = TestHelpers.GetOptions();
            options.ProcessingBlockBoundedCapacity = 10;
            options.MaxDegreeOfParallelismForStreamResourcePages = 1;

            var block = new StreamResourcePagesBlockFactory(handler, A.Fake<IRunSummaryCollector>()).CreateBlock<object>(options, new BufferBlock<ErrorItemMessage>());
            var cancellation = new CancellationTokenSource();

            block.Post(new StreamResourcePageMessage<object> { ResourceUrl = "/ed-fi/students", CancellationSource = cancellation }).ShouldBeTrue();

            // No consumer: the handler parks in SendAsync once the buffer is full
            int stalledAt = await GetStableValueAsync(() => handler.Produced);
            stalledAt.ShouldBeInRange(10, 11);

            // Cancelling the resource must release the parked send and abandon the rest of the page message
            cancellation.Cancel();
            block.Complete();

            int consumed = 0;
            var sink = new ActionBlock<object>(_ => Interlocked.Increment(ref consumed));
            block.LinkTo(sink, new DataflowLinkOptions { PropagateCompletion = true });

            await sink.Completion.WaitAsync(TimeSpan.FromSeconds(30));

            // Only what was already buffered is delivered; the handler was not pulled to the end of its sequence
            consumed.ShouldBeLessThanOrEqualTo(stalledAt);
            handler.Produced.ShouldBeLessThanOrEqualTo(stalledAt + 1);
        }

        [Test]
        public async Task Pages_block_should_not_stall_when_bounding_is_disabled()
        {
            var handler = new CountingPageHandler();
            var options = TestHelpers.GetOptions();
            options.ProcessingBlockBoundedCapacity = -1;

            var block = new StreamResourcePagesBlockFactory(handler, A.Fake<IRunSummaryCollector>()).CreateBlock<object>(options, new BufferBlock<ErrorItemMessage>());

            block.Post(new StreamResourcePageMessage<object> { ResourceUrl = "/ed-fi/students", CancellationSource = new CancellationTokenSource() }).ShouldBeTrue();

            (await GetStableValueAsync(() => handler.Produced)).ShouldBe(ItemsPerMessage);
        }

        /// <summary>
        /// Pins the ActionBlock -&gt; item buffer -&gt; downstream fault path that the handler's authentication rethrow
        /// depends on: a throwing page sequence must fault the encapsulated block and every linked target.
        /// </summary>
        [Test]
        public async Task Pages_block_should_fault_itself_and_its_targets_when_a_page_handler_throws()
        {
            var options = TestHelpers.GetOptions();

            var block = new StreamResourcePagesBlockFactory(new ThrowingPageHandler(), A.Fake<IRunSummaryCollector>())
                .CreateBlock<object>(options, new BufferBlock<ErrorItemMessage>());

            var sink = new ActionBlock<object>(_ => { });
            block.LinkTo(sink, new DataflowLinkOptions { PropagateCompletion = true });

            block.Post(new StreamResourcePageMessage<object> { ResourceUrl = "/ed-fi/students", CancellationSource = new CancellationTokenSource() }).ShouldBeTrue();

            // The buffer is faulted with the unwrapped exception, so the block reports it directly rather than
            // as a nested AggregateException
            var blockException = await Should.ThrowAsync<InvalidOperationException>(() => block.Completion.WaitAsync(TimeSpan.FromSeconds(30)));
            blockException.Message.ShouldContain("authenticate");

            // Dataflow's own completion propagation re-wraps for a linked target; what matters is that the
            // target faults and the original cause survives
            var sinkException = await Should.ThrowAsync<Exception>(() => sink.Completion.WaitAsync(TimeSpan.FromSeconds(30)));
            sinkException.GetBaseException().ShouldBeOfType<InvalidOperationException>();
            sink.Completion.IsFaulted.ShouldBeTrue();
        }

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
    }
}
