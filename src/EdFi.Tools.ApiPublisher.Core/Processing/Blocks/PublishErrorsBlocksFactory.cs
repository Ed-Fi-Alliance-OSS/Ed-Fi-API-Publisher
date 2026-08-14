// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using Serilog;
using System;
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
            var publishErrorsIngestionBlock = new BatchBlock<ErrorItemMessage>(
                options.ErrorPublishingBatchSize,
                new GroupingDataflowBlockOptions { BoundedCapacity = options.ResolvedErrorPublishingBoundedCapacity });

            var publishErrorsCompletionBlock = CreatePublishErrorsBlock(
                _errorPublisher,
                boundedCapacity: options.ResolvedErrorPublishingBoundedCapacity == -1
                    ? DataflowBlockOptions.Unbounded
                    : MaxQueuedErrorBatches);

            publishErrorsIngestionBlock.LinkTo(publishErrorsCompletionBlock, new DataflowLinkOptions { PropagateCompletion = true });

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
