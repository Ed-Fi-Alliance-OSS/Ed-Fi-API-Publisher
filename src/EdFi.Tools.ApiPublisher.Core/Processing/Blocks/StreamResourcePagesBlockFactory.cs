// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Metadata;
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
        private readonly IRunSummaryCollector _runSummaryCollector;

        private readonly ILogger _logger = Log.ForContext(typeof(StreamResourcePagesBlockFactory));

        public StreamResourcePagesBlockFactory(
            IStreamResourcePageMessageHandler streamResourcePageMessageHandler,
            IRunSummaryCollector runSummaryCollector)
        {
            _streamResourcePageMessageHandler = streamResourcePageMessageHandler;
            _runSummaryCollector = runSummaryCollector;
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
            // Counted here because this is the one place every document passes through on its way to the
            // target, and it is the only measure of attempted publishing the run has: the processing blocks
            // downstream report errors, never successes. Items stream through lazily, so the count accrues as
            // each one is handed to the buffer and is recorded once the page message is done (see APIPUB-120).
            long attemptedItemCount = 0;

            try
            {
                await foreach (var pageItem in _streamResourcePageMessageHandler
                    .HandleStreamResourcePageAsync(msg, options, errorHandlingBlock)
                    .ConfigureAwait(false))
                {
                    // Waits while the buffer is full; the resource's token releases a parked handler on cancellation
                    if (!await pageItemsBuffer.SendAsync(pageItem, msg.CancellationSource.Token).ConfigureAwait(false))
                    {
                        _logger.Warning("{ResourceUrl}: The page items buffer declined an item (completed or faulted); abandoning the remainder of the page message (a whole partition under cursor paging).", msg.ResourceUrl);

                        return;
                    }

                    // A message is one document for every API target, but the SQLite target writes a page at a
                    // time, so it says how many documents its message carries. Counting messages there would
                    // report pages as documents (see APIPUB-120).
                    attemptedItemCount += pageItem is IItemCountedProcessDataMessage counted ? counted.ItemCount : 1;
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
            finally
            {
                // The authorization retry pass re-reads a resource the first pass already counted, under the
                // same resource URL, so counting it again would report every document of that resource twice
                // (see APIPUB-120). A document the first pass deferred with a 403 is published by this pass,
                // which is why it stays in the attempted total.
                if (attemptedItemCount > 0 && !msg.IsAuthorizationRetryPass)
                {
                    _runSummaryCollector.AddAttemptedItems(msg.ResourceUrl, attemptedItemCount);
                }
            }
        }
    }
}
