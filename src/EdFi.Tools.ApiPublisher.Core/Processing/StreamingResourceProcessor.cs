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
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks.Dataflow;

namespace EdFi.Tools.ApiPublisher.Core.Processing;

public interface IStreamingResourceProcessor
{
    IDictionary<string, StreamingPagesItem> Start<TProcessDataMessage>(
        Func<CreateBlocksRequest, (ITargetBlock<TProcessDataMessage>, ISourceBlock<ErrorItemMessage>)> createProcessingBlocks,
        Func<StreamResourcePageMessage<TProcessDataMessage>, TextReader, Action<int>, IEnumerable<TProcessDataMessage>> createProcessDataMessages,
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
        Func<StreamResourcePageMessage<TProcessDataMessage>, TextReader, Action<int>, IEnumerable<TProcessDataMessage>> createProcessDataMessages,
        ProcessingContext processingContext,
        CancellationToken cancellationToken)
    {
        _logger.Information($"Initiating resource streaming.");

        var linkOptions = new DataflowLinkOptions { PropagateCompletion = true };

        var streamingPagesByResourceKey = new Dictionary<string, StreamingPagesItem>(StringComparer.OrdinalIgnoreCase);

        var streamingResourceBlockByResourceKey =
            new Dictionary<string, ITargetBlock<StreamResourceMessage>>(StringComparer.OrdinalIgnoreCase);

        // Resource paths that have an authorization-retry ("#Retry") pipeline which re-publishes the entire
        // resource after its update prerequisites complete. A 403 on an individual item of such a resource is
        // skipped (no error published) because the retry pass covers it (see APIPUB-133).
        var retryPipelineResourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                processingContext.JavaScriptModuleFactory);

            // This creates the actual processing sub-pipeline ingesting TProcessDataMessage through to ErrorItemMessages
            var (processingInputBlock, processingOutputBlock) = createProcessingBlocks(createBlocksRequest);

            if (isRetryPipeline)
            {
                retryPipelineResourcePaths.Add(resourcePath);
            }

            streamingPagesByResourceKey.Add(resourceKey, new StreamingPagesItem { CompletionBlock = processingOutputBlock });

            // Create a new StreamResource block for the resource
            TransformManyBlock<StreamResourceMessage, StreamResourcePageMessage<TProcessDataMessage>> streamResourceBlock =
                _streamResourceBlockFactory.CreateBlock(createProcessDataMessages, processingContext.PublishErrorsIngestionBlock, processingContext.Options, cancellationToken);

            // Create a new StreamResourcePages block
            TransformManyBlock<StreamResourcePageMessage<TProcessDataMessage>, TProcessDataMessage> streamResourcePagesBlock =
                _streamResourcePagesBlockFactory.CreateBlock<TProcessDataMessage>(
                    processingContext.Options,
                    processingContext.PublishErrorsIngestionBlock);

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

            // Looked up by resource KEY so only the main resource is flagged -- the "#Retry" pseudo-resource
            // itself is not, meaning a repeat 403 during the retry pass surfaces as an error (or a warning
            // under TreatForbiddenPostAsWarning) rather than deferring again.
            bool hasAuthorizationRetryPipeline = retryPipelineResourcePaths.Contains(resourceKey);

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
                HasAuthorizationRetryPipeline = hasAuthorizationRetryPipeline,
                ProcessingSemaphore = processingContext.Semaphore,
            };

            if (hasAuthorizationRetryPipeline)
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
