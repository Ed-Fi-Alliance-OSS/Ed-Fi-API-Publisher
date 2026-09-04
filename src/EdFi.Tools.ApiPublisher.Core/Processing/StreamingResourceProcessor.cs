// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Helpers;
using EdFi.Tools.ApiPublisher.Core.Processing.Blocks;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using Serilog;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks.Dataflow;

namespace EdFi.Tools.ApiPublisher.Core.Processing;

public interface IStreamingResourceProcessor
{
    IDictionary<string, StreamingPagesItem> Start<TProcessDataMessage>(
        Func<CreateBlocksRequest, (ITargetBlock<TProcessDataMessage>, ISourceBlock<ErrorItemMessage>)> createProcessingBlocks,
        Func<StreamResourcePageMessage<TProcessDataMessage>, string, IEnumerable<TProcessDataMessage>> createProcessDataMessages,
        ProcessingContext processingContext,
        CancellationToken cancellationToken);
}

public class StreamingResourceProcessor : IStreamingResourceProcessor
{
    private readonly StreamResourceBlockFactory _streamResourceBlockFactory;
    private readonly StreamResourcePagesBlockFactory _streamResourcePagesBlockFactory;

    private readonly ISourceConnectionDetails _sourceConnectionDetails;

    private readonly ILogger _logger = Log.ForContext(typeof(StreamingResourceProcessor));

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamingResourceProcessor"/> class using the supplied TPL blocks and
    /// item action factory functions.
    /// </summary>
    /// <param name="streamResourceBlockFactory"></param>
    /// <param name="streamResourcePagesBlockFactory"></param>
    /// <param name="sourceConnectionDetails"></param>
    public StreamingResourceProcessor(
        StreamResourceBlockFactory streamResourceBlockFactory,
        StreamResourcePagesBlockFactory streamResourcePagesBlockFactory,
        ISourceConnectionDetails sourceConnectionDetails)
    {
        _streamResourceBlockFactory = streamResourceBlockFactory;
        _streamResourcePagesBlockFactory = streamResourcePagesBlockFactory;
        _sourceConnectionDetails = sourceConnectionDetails;
    }

    public IDictionary<string, StreamingPagesItem> Start<TProcessDataMessage>(
        Func<CreateBlocksRequest, (ITargetBlock<TProcessDataMessage>, ISourceBlock<ErrorItemMessage>)> createProcessingBlocks,
        Func<StreamResourcePageMessage<TProcessDataMessage>, string, IEnumerable<TProcessDataMessage>> createProcessDataMessages,
        ProcessingContext processingContext,
        CancellationToken cancellationToken)
    {
        _logger.Information($"Initiating resource streaming.");

        var linkOptions = new DataflowLinkOptions { PropagateCompletion = true };

        var streamingPagesByResourceKey = new Dictionary<string, StreamingPagesItem>(StringComparer.OrdinalIgnoreCase);

        var streamingResourceBlockByResourceKey =
            new Dictionary<string, ITargetBlock<StreamResourceMessage>>(StringComparer.OrdinalIgnoreCase);

        var postAuthorizationRetryByResourceKey = new Dictionary<string, Action<object>>(StringComparer.OrdinalIgnoreCase);

        // Set up streaming resource blocks for all resources
        foreach (var kvp in processingContext.DependencyKeysByResourceKey)
        {
            string resourceKey = kvp.Key;
            string resourcePath = ResourcePathHelper.GetResourcePath(resourceKey);

            // Is this an authorization retry "resource"?
            bool isRetryPipeline = resourceKey.EndsWith(Conventions.RetryKeySuffix);

            var createBlocksRequest = new CreateBlocksRequest(
                processingContext.Options,
                processingContext.AuthorizationFailureHandling,
                processingContext.PublishErrorsIngestionBlock,
                processingContext.JavaScriptModuleFactory)
            {
                IsRetryPipeline = isRetryPipeline,
            };

            // This creates the actual processing sub-pipeline ingesting TProcessDataMessage through to ErrorItemMessages
            var (processingInputBlock, processingOutputBlock) = createProcessingBlocks(createBlocksRequest);

            if (isRetryPipeline)
            {
                // Save an action delegate for processing the item, keyed by the resource path.
                // NOTE: Retry pipeline blocks are never bounded, so the Post here can only be declined if the
                // block has already completed (see APIPUB-112).
                postAuthorizationRetryByResourceKey.Add(
                    resourcePath,
                    msg =>
                    {
                        if (!processingInputBlock.Post((TProcessDataMessage)msg))
                        {
                            _logger.Error(
                                "{ResourcePath}: Authorization retry message was declined by the retry processing block and will not be reprocessed.",
                                resourcePath);
                        }
                    });
            }

            streamingPagesByResourceKey.Add(resourceKey, new StreamingPagesItem { CompletionBlock = processingOutputBlock });

            // Create a new StreamResource block for the resource
            TransformManyBlock<StreamResourceMessage, StreamResourcePageMessage<TProcessDataMessage>> streamResourceBlock =
                _streamResourceBlockFactory.CreateBlock(createProcessDataMessages, processingContext.PublishErrorsIngestionBlock, processingContext.Options, cancellationToken);

            // Create a new StreamResourcePages block (unbounded for retry pipelines -- see CreateBlocksRequest.IsRetryPipeline)
            TransformManyBlock<StreamResourcePageMessage<TProcessDataMessage>, TProcessDataMessage> streamResourcePagesBlock =
                _streamResourcePagesBlockFactory.CreateBlock<TProcessDataMessage>(
                    processingContext.Options,
                    processingContext.PublishErrorsIngestionBlock,
                    applyBoundedCapacity: !isRetryPipeline);

            // Link together the general pipeline
            streamResourceBlock.LinkTo(streamResourcePagesBlock, linkOptions);
            streamResourcePagesBlock.LinkTo(processingInputBlock, linkOptions);
            processingOutputBlock.LinkTo(processingContext.PublishErrorsIngestionBlock, new DataflowLinkOptions { Append = true });

            streamingResourceBlockByResourceKey.Add(resourceKey, streamResourceBlock);
        }

        // Linked to the run's token so external cancellation also releases producers parked on bounded
        // blocks via the per-message cancellation source (see APIPUB-112)
        var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Initiate streaming of all resources, with dependencies
        foreach (var kvp in processingContext.DependencyKeysByResourceKey)
        {
            var resourceKey = kvp.Key;
            var resourcePath = ResourcePathHelper.GetResourcePath(resourceKey);
            var dependencyPaths = kvp.Value.ToArray();

            // TODO: API-specific representation, perhaps should just be renamed to "key" since it's not being used specifically for HTTP request
            string resourceUrl = $"{resourcePath}{processingContext.ResourceUrlPathSuffix}";

            if (cancellationSource.IsCancellationRequested)
            {
                _logger.Debug($"{resourceUrl}: Cancellation requested -- resource will not be streamed.");

                break;
            }

            // Record the dependencies for status reporting
            streamingPagesByResourceKey[resourceKey].DependencyPaths = dependencyPaths;

            postAuthorizationRetryByResourceKey.TryGetValue(resourceKey, out Action<object> postRetry);

            var skippedResources = ResourcePathHelper.ParseResourcesCsvToResourcePathArray(_sourceConnectionDetails.ExcludeOnly);

            var message = new StreamResourceMessage
            {
                // EdFiApiClient = sourceApiClient,
                ResourceUrl = resourceUrl,
                ShouldSkip = skippedResources.Contains(resourcePath),
                Dependencies = dependencyPaths.Select(p => streamingPagesByResourceKey[p].CompletionBlock.Completion).ToArray(),
                DependencyPaths = dependencyPaths.ToArray(),
                PageSize = processingContext.Options.StreamingPageSize,
                ChangeWindow = processingContext.ChangeWindow,
                CancellationSource = cancellationSource,
                PostAuthorizationFailureRetry = postRetry,
                ProcessingSemaphore = processingContext.Semaphore,
            };

            if (postRetry != null)
            {
                _logger.Debug($"{message.ResourceUrl}: Authorization retry processing is supported.");
            }

            var streamingBlock = streamingResourceBlockByResourceKey[resourceKey];

            if (_logger.IsEnabled(LogEventLevel.Debug))
            {
                _logger.Debug($"{message.ResourceUrl}: Sending message to initiate streaming.");
            }

            streamingBlock.Post(message);
            streamingBlock.Complete();
        }

        return streamingPagesByResourceKey;
    }
}
