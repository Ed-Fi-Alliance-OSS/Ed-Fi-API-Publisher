using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Extensions;
using EdFi.Tools.ApiPublisher.Core.Helpers;
using EdFi.Tools.ApiPublisher.Core.Processing.Handlers;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using log4net;
using Newtonsoft.Json.Linq;
using Polly;
using Polly.Contrib.WaitAndRetry;

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
        
        public TransformManyBlock<StreamResourcePageMessage<TItemActionMessage>, TItemActionMessage> CreateBlock<TItemActionMessage>(
            Options options, 
            ITargetBlock<ErrorItemMessage> errorHandlingBlock)
        {
            var streamResourcePagesBlock =
                new TransformManyBlock<StreamResourcePageMessage<TItemActionMessage>, TItemActionMessage>(
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