using System;
using System.Threading.Tasks.Dataflow;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using log4net;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Blocks
{
    public static class PublishErrors
    {
        private static readonly ILog _logger = LogManager.GetLogger(typeof(PublishErrors));
        
        public static ValueTuple<ITargetBlock<ErrorItemMessage>, ActionBlock<ErrorItemMessage[]>> GetBlocks(
            Options options,
            IErrorPublisher errorPublisher)
        {
            var publishErrorsIngestionBlock = new BatchBlock<ErrorItemMessage>(options.ErrorPublishingBatchSize);
            var publishErrorsCompletionBlock = CreatePublishErrorsBlock(errorPublisher);
            publishErrorsIngestionBlock.LinkTo(publishErrorsCompletionBlock, new DataflowLinkOptions {PropagateCompletion = true});

            return ((ITargetBlock<ErrorItemMessage>) publishErrorsIngestionBlock, publishErrorsCompletionBlock);
        }
        
        private static ActionBlock<ErrorItemMessage[]> CreatePublishErrorsBlock(IErrorPublisher errorPublisher)
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