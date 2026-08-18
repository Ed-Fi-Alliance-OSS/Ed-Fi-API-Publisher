// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using Serilog;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Blocks
{
    public class PublishErrorsBlocksFactory
    {
        // Maximum number of already-formed error batches allowed to queue for publication before the
        // (bounded) ingestion block starts postponing new errors.
        private const int MaxQueuedErrorBatches = 4;

        private static readonly ILogger _logger = Log.Logger.ForContext(typeof(PublishErrorsBlocksFactory));
        private IErrorPublisher _errorPublisher;

        /// <summary>
        /// Gets the first exception thrown by the error publisher during this run, if any. The publishing
        /// block never faults on a publisher failure (a faulted sink would sever its links and strand
        /// producers -- see CreatePublishErrorsBlock); the failure is recorded here instead so the run
        /// can be failed after the pipeline drains.
        /// </summary>
        public Exception FirstPublishingException { get; private set; }

        public PublishErrorsBlocksFactory(IErrorPublisher errorPublisher)
        {
            _errorPublisher = errorPublisher;
        }

        public ValueTuple<ITargetBlock<ErrorItemMessage>, ActionBlock<ErrorItemMessage[]>> CreateBlocks(Options options)
        {
            // Bound the error path so errors produced faster than they can be published (e.g. a sustained
            // authorization-failure storm) exert backpressure on the pipeline instead of queueing without
            // limit (see APIPUB-112). Producers must use SendAsync (never Post) so a full ingestion block
            // delays them rather than silently dropping the error; linked processing blocks postpone offers.
            // CLI options validation rejects a batch size below 1; the clamp here only protects library
            // consumers that bypass that validation, since BatchBlock throws for non-positive batch sizes.
            var publishErrorsIngestionBlock = new BatchBlock<ErrorItemMessage>(
                Math.Max(1, options.ErrorPublishingBatchSize),
                new GroupingDataflowBlockOptions { BoundedCapacity = options.ResolvedErrorPublishingBoundedCapacity });

            var publishErrorsCompletionBlock = CreatePublishErrorsBlock(
                _errorPublisher,
                boundedCapacity: options.ResolvedErrorPublishingBoundedCapacity == -1
                    ? DataflowBlockOptions.Unbounded
                    : Options.MaxQueuedErrorBatches);

            publishErrorsIngestionBlock.LinkTo(publishErrorsCompletionBlock, new DataflowLinkOptions { PropagateCompletion = true });

            // Dataflow only propagates completion forward, so a faulted publisher would sever the link and
            // leave the bounded ingestion block permanently full, parking every SendAsync producer forever.
            // Propagate the fault backward so parked sends complete (declined) and the run fails instead of hanging.
            publishErrorsCompletionBlock.Completion.ContinueWith(
                t => ((IDataflowBlock)publishErrorsIngestionBlock).Fault(t.Exception!),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return (publishErrorsIngestionBlock, publishErrorsCompletionBlock);
        }

        private ActionBlock<ErrorItemMessage[]> CreatePublishErrorsBlock(IErrorPublisher errorPublisher, int boundedCapacity)
        {
            return new ActionBlock<ErrorItemMessage[]>(async errors =>
            {
                try
                {
                    await errorPublisher.PublishErrorsAsync(errors)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Never rethrow: a faulted sink severs its incoming links, permanently declining the
                    // processing-output blocks linked to the ingestion block -- their buffered errors could
                    // never drain, so those blocks would never complete and the run would spin forever
                    // (found in review; a backward-faulting variant hung the same way through the linked
                    // producers). Record the first failure so ChangeProcessor can fail the run after the
                    // pipeline drains, and keep consuming batches -- the error store may recover, and
                    // producers must never be stranded. (No lock needed: the block runs with parallelism 1.)
                    if (FirstPublishingException is null)
                    {
                        FirstPublishingException = ex;
                        _logger.Fatal($"Unable to publish errors due to an unhandled exception (the run will be failed once processing completes): {ex}");
                    }
                    else
                    {
                        _logger.Error($"Unable to publish errors due to an unhandled exception: {ex}");
                    }
                }
            },
            new ExecutionDataflowBlockOptions { BoundedCapacity = boundedCapacity });
        }
    }
}
