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
