// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing.Handlers;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using Polly.RateLimit;
using Serilog;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Blocks
{
    public class StreamResourcePagesBlockFactory
    {
        private readonly IStreamResourcePageMessageHandler _streamResourcePageMessageHandler;

        private readonly ILogger _logger = Log.ForContext(typeof(StreamResourcePagesBlockFactory));

        public StreamResourcePagesBlockFactory(IStreamResourcePageMessageHandler streamResourcePageMessageHandler)
        {
            _streamResourcePageMessageHandler = streamResourcePageMessageHandler;
        }

        /// <summary>
        /// Creates the block that turns page messages into item messages. Items pass through a bounded buffer
        /// before reaching the processing block: each page handler awaits SendAsync per item, so when the buffer
        /// is full (the target is slower than the source) the handler stops pulling its item sequence and thus
        /// stops fetching pages -- backpressure that holds even when one page message expands into a whole
        /// partition of pages (cursor paging, see APIPUB-139). A TransformManyBlock cannot provide this, even
        /// with the IAsyncEnumerable overload: Dataflow bounding gates input acceptance only, so an in-progress
        /// expansion is never paused (pinned by StreamResourcePagesBlockBackpressureTests).
        /// </summary>
        public IPropagatorBlock<StreamResourcePageMessage<TProcessDataMessage>, TProcessDataMessage> CreateBlock<TProcessDataMessage>(
            Options options,
            ITargetBlock<ErrorItemMessage> errorHandlingBlock)
        {
            var pageItemsBuffer = new BufferBlock<TProcessDataMessage>(
                new DataflowBlockOptions
                {
                    // Item-denominated (see APIPUB-112); -1 disables the bound
                    BoundedCapacity = options.ResolvedProcessingBlockBoundedCapacity,
                });

            var pagesBlock = new ActionBlock<StreamResourcePageMessage<TProcessDataMessage>>(
                msg => PumpPageItemsAsync(msg, options, errorHandlingBlock, pageItemsBuffer),
                new ExecutionDataflowBlockOptions
                {
                    MaxDegreeOfParallelism = options.MaxDegreeOfParallelismForStreamResourcePages,

                    // Page messages are lightweight; this bound only caps how many wait to be handled (queued
                    // plus in progress), it is the item buffer above that bounds memory
                    BoundedCapacity = options.ResolvedStreamResourcePagesBlockBoundedCapacity,
                });

            // Completion (or a fault) of page handling flows to the item buffer, and from there downstream.
            // The fault is unwrapped from the task's AggregateException so the buffer -- and every block that
            // takes its completion from it -- reports the original exception rather than a nested aggregate.
            pagesBlock.Completion.ContinueWith(
                t =>
                {
                    if (t.IsFaulted)
                    {
                        ((IDataflowBlock)pageItemsBuffer).Fault(t.Exception.GetBaseException());
                    }
                    else
                    {
                        pageItemsBuffer.Complete();
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return DataflowBlock.Encapsulate(pagesBlock, pageItemsBuffer);
        }

        private async Task PumpPageItemsAsync<TProcessDataMessage>(
            StreamResourcePageMessage<TProcessDataMessage> msg,
            Options options,
            ITargetBlock<ErrorItemMessage> errorHandlingBlock,
            ITargetBlock<TProcessDataMessage> pageItemsBuffer)
        {
            try
            {
                await foreach (var pageItem in _streamResourcePageMessageHandler
                    .HandleStreamResourcePageAsync(msg, options, errorHandlingBlock)
                    .ConfigureAwait(false))
                {
                    // Waits while the buffer is full; the resource's token releases a parked handler on cancellation
                    if (!await pageItemsBuffer.SendAsync(pageItem, msg.CancellationSource.Token).ConfigureAwait(false))
                    {
                        _logger.Warning("{ResourceUrl}: The page items buffer declined an item (completed or faulted); abandoning the remainder of the page.", msg.ResourceUrl);

                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (msg.CancellationSource.IsCancellationRequested)
            {
                _logger.Debug("{ResourceUrl}: Cancellation requested while delivering page items.", msg.ResourceUrl);
            }
            catch (RateLimitRejectedException ex)
            {
                _logger.Fatal(ex, "{ResourceUrl}: Rate limit exceeded. Please try again later.", msg.ResourceUrl);
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error($"{msg.ResourceUrl}: An unhandled exception occurred in the StreamResourcePages block: {ex}");
                throw;
            }
        }
    }
}
