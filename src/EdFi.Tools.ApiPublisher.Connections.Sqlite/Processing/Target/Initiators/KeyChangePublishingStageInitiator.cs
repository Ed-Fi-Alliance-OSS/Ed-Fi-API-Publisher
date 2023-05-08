// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Target.Messages;
using EdFi.Tools.ApiPublisher.Core.Processing;

namespace EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Target.Initiators;

public class KeyChangePublishingStageInitiator : IPublishingStageInitiator
{
    private readonly IProcessingBlocksFactory<KeyChangesJsonMessage> _processingBlocksFactory;
    private readonly IStreamingResourceProcessor _streamingResourceProcessor;

    public KeyChangePublishingStageInitiator(
        IStreamingResourceProcessor streamingResourceProcessor,
        IProcessingBlocksFactory<KeyChangesJsonMessage> processingBlocksFactory)
    {
        _streamingResourceProcessor = streamingResourceProcessor;
        _processingBlocksFactory = processingBlocksFactory;
    }

    public IDictionary<string, StreamingPagesItem> Start(ProcessingContext processingContext, CancellationToken cancellationToken)
    {
        return _streamingResourceProcessor.Start(
            _processingBlocksFactory.CreateProcessingBlocks,
            _processingBlocksFactory.CreateProcessDataMessages,
            processingContext,
            cancellationToken);
    }
}
