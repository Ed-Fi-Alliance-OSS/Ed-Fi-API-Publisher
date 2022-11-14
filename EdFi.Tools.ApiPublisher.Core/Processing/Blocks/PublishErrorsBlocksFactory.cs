using System;
using System.Threading.Tasks.Dataflow;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using log4net;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Blocks
{
    public class PublishErrorsBlocksFactory
    {
        private readonly IErrorPublisher _errorPublisher;
        
        private readonly ILog _logger = LogManager.GetLogger(typeof(PublishErrorsBlocksFactory));

        public PublishErrorsBlocksFactory(IErrorPublisher errorPublisher)
        {
            _errorPublisher = errorPublisher;
        }
        
        public ValueTuple<ITargetBlock<ErrorItemMessage>, ActionBlock<ErrorItemMessage[]>> CreateBlocks(Options options)
        {
            var publishErrorsIngestionBlock = new BatchBlock<ErrorItemMessage>(options.ErrorPublishingBatchSize);
            var publishErrorsCompletionBlock = CreatePublishErrorsBlock(_errorPublisher);
            
            publishErrorsIngestionBlock.LinkTo(publishErrorsCompletionBlock, new DataflowLinkOptions {PropagateCompletion = true});

            return (publishErrorsIngestionBlock, publishErrorsCompletionBlock);
        }
        
        private ActionBlock<ErrorItemMessage[]> CreatePublishErrorsBlock(IErrorPublisher errorPublisher)
        {
            return new ActionBlock<ErrorItemMessage[]>(async errors =>
            {
                try
                {
                    await errorPublisher.PublishErrorsAsync(errors)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Error($"Unable to publish errors due to an unexpected exception: {ex}");
                }
            });
        }
    }
}