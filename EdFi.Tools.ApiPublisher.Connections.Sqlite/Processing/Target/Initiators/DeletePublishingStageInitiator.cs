using EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Target.Messages;
using EdFi.Tools.ApiPublisher.Core.Processing;

namespace EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Target.Initiators;

public class DeletePublishingStageInitiator : IPublishingStageInitiator
{
    private readonly IProcessingBlocksFactory<DeletesJsonMessage> _processingBlocksFactory;
    private readonly IStreamingResourceProcessor _streamingResourceProcessor;

    public DeletePublishingStageInitiator(
        IStreamingResourceProcessor streamingResourceProcessor,
        IProcessingBlocksFactory<DeletesJsonMessage> processingBlocksFactory)
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
