using System;
using System.Threading.Tasks.Dataflow;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing.Handlers;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using log4net;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Blocks
{
    public class StreamResourcePagesBlockFactory
    {
        private readonly IStreamResourcePageMessageHandler _streamResourcePageMessageHandler;
        
        private readonly ILog _logger = LogManager.GetLogger(typeof(StreamResourcePagesBlockFactory));

        public StreamResourcePagesBlockFactory(IStreamResourcePageMessageHandler streamResourcePageMessageHandler)
        {
            _streamResourcePageMessageHandler = streamResourcePageMessageHandler;
        }
        
        public TransformManyBlock<StreamResourcePageMessage<TProcessDataMessage>, TProcessDataMessage> CreateBlock<TProcessDataMessage>(
            Options options, 
            ITargetBlock<ErrorItemMessage> errorHandlingBlock)
        {
            var streamResourcePagesBlock =
                new TransformManyBlock<StreamResourcePageMessage<TProcessDataMessage>, TProcessDataMessage>(
                    async msg =>
                    {
                        try
                        {
                            return await _streamResourcePageMessageHandler.HandleStreamResourcePageAsync(msg, options, errorHandlingBlock).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.Error($"{msg.ResourceUrl}: An unhandled exception occurred in the StreamResourcePages block: {ex}");
                            throw;
                        }
                    },
                    new ExecutionDataflowBlockOptions
                    {
                        MaxDegreeOfParallelism = options.MaxDegreeOfParallelismForStreamResourcePages
                    });
            
            return streamResourcePagesBlock;
        }
   }
}