using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Processing.Initiators;

public class UpsertPublishingStageInitiator : IPublishingStageInitiator
{
    private readonly IStreamingResourceProcessor _streamingResourceProcessor;
    private readonly IProcessDataPipelineFactory<PostItemMessage> _processDataPipelineFactory;

    public UpsertPublishingStageInitiator(IStreamingResourceProcessor streamingResourceProcessor, IProcessDataPipelineFactory<PostItemMessage> processDataPipelineFactory)
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
