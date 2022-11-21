// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Processing.Initiators;

public class DeletePublishingStageInitiator : IPublishingStageInitiator
{
    private readonly IProcessDataPipelineFactory<GetItemForDeletionMessage> _processDataPipelineFactory;
    private readonly IStreamingResourceProcessor _streamingResourceProcessor;

    public DeletePublishingStageInitiator(
        IStreamingResourceProcessor streamingResourceProcessor,
        IProcessDataPipelineFactory<GetItemForDeletionMessage> processDataPipelineFactory)
    {
        _streamingResourceProcessor = streamingResourceProcessor;
        _processDataPipelineFactory = processDataPipelineFactory;
    }

    public IDictionary<string, StreamingPagesItem> Start(ProcessingContext processingContext, CancellationToken cancellationToken)
    {
        return _streamingResourceProcessor.Start(
            _processDataPipelineFactory.CreateProcessingBlocks,
            _processDataPipelineFactory.CreateProcessDataMessages,
            processingContext,
            cancellationToken);
    }
}
