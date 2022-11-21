using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Target.Processing.Initiators;

public class UpsertPublishingStageInitiator : IPublishingStageInitiator
{
    private readonly IStreamingResourceProcessor _streamingResourceProcessor;
    private readonly IProcessingBlocksFactory<PostItemMessage> _processingBlocksFactory;

    public UpsertPublishingStageInitiator(IStreamingResourceProcessor streamingResourceProcessor, IProcessingBlocksFactory<PostItemMessage> processingBlocksFactory)
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
