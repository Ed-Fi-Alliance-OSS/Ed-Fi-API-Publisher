using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Processing.Initiators;

public class ChangeKeysPublishingStageInitiator : IPublishingStageInitiator
{
    private readonly IProcessDataPipelineFactory<GetItemForKeyChangeMessage> _processDataPipelineFactory;
    private readonly IStreamingResourceProcessor _streamingResourceProcessor;

    public ChangeKeysPublishingStageInitiator(
        IStreamingResourceProcessor streamingResourceProcessor,
        IProcessDataPipelineFactory<GetItemForKeyChangeMessage> processDataPipelineFactory)
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
