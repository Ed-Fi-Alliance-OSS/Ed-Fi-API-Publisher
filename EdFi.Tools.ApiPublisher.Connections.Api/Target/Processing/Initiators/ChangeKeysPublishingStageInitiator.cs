using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Target.Processing.Initiators;

public class ChangeKeysPublishingStageInitiator : IPublishingStageInitiator
{
    private readonly IProcessingBlocksFactory<GetItemForKeyChangeMessage> _processingBlocksFactory;
    private readonly IStreamingResourceProcessor _streamingResourceProcessor;

    public ChangeKeysPublishingStageInitiator(
        IStreamingResourceProcessor streamingResourceProcessor,
        IProcessingBlocksFactory<GetItemForKeyChangeMessage> processingBlocksFactory)
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
