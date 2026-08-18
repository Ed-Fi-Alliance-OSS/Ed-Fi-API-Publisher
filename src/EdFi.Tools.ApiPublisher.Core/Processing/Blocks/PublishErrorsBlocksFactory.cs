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
                    : MaxQueuedErrorBatches);

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
                    _logger.Error($"Unable to publish errors due to an unhandled exception: {ex}");

                    throw;
                }
            },
            new ExecutionDataflowBlockOptions { BoundedCapacity = boundedCapacity });
        }
    }
}
