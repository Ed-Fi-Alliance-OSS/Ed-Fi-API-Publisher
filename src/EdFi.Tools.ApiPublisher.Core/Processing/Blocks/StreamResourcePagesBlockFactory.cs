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

        public TransformManyBlock<StreamResourcePageMessage<TProcessDataMessage>, TProcessDataMessage> CreateBlock<TProcessDataMessage>(
            Options options,
            ITargetBlock<ErrorItemMessage> errorHandlingBlock,
            bool applyBoundedCapacity = true)
        {
            var streamResourcePagesBlock =
                new TransformManyBlock<StreamResourcePageMessage<TProcessDataMessage>, TProcessDataMessage>(
                    async msg =>
                    {
                        try
                        {
                            return await _streamResourcePageMessageHandler.HandleStreamResourcePageAsync(msg, options, errorHandlingBlock).ConfigureAwait(false);
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
                    },
                    new ExecutionDataflowBlockOptions
                    {
                        MaxDegreeOfParallelism = options.MaxDegreeOfParallelismForStreamResourcePages,

                        // Without a bound, this block's output buffer absorbs every page of source items the
                        // moment it is fetched, growing without limit whenever the target is slower than the
                        // source (see APIPUB-112). The bound is denominated in page messages: it gates how many
                        // pages may be accepted (and therefore fetched), since each accepted page expands into
                        // a full page of items regardless of how full the block's output buffer already is.
                        BoundedCapacity = applyBoundedCapacity
                            ? options.ResolvedStreamResourcePagesBlockBoundedCapacity
                            : DataflowBlockOptions.Unbounded,
                    });

            return streamResourcePagesBlock;
        }
    }
}
