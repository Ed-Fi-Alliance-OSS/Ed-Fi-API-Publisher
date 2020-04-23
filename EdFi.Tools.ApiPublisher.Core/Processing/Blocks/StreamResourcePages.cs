using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Helpers;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using log4net;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Blocks
{
    public static class StreamResourcePages
    {
        private static readonly ILog _logger = LogManager.GetLogger(typeof(StreamResourcePages));
        
        public static TransformManyBlock<StreamResourcePageMessage<TItemActionMessage>, TItemActionMessage> GetBlock<TItemActionMessage>(Options options)
        {
            var streamResourcePagesBlock =
                new TransformManyBlock<StreamResourcePageMessage<TItemActionMessage>, TItemActionMessage>(
                    async msg =>
                    {
                        try
                        {
                            return await StreamResourcePage(msg).ConfigureAwait(false);
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
        
        private static async Task<IEnumerable<TItemActionMessage>> StreamResourcePage<TItemActionMessage>(
            StreamResourcePageMessage<TItemActionMessage> message)
        {
            long offset = message.Offset;
            int limit = message.Limit;

            string changeWindowParms = RequestHelper.GetChangeWindowParms(message.ChangeWindow);
            
            try
            {
                var transformedMessages = new List<TItemActionMessage>();
                
                do
                {
                    if (message.CancellationSource.IsCancellationRequested)
                    {
                        _logger.Debug($"{message.ResourceUrl}: Cancellation requested while processing page of source items starting at offset {offset}.");
                        return Enumerable.Empty<TItemActionMessage>();
                    }
                    
                    if (_logger.IsDebugEnabled)
                    {
                        _logger.Debug($"{message.ResourceUrl}: Retrieving page items {offset} to {offset + limit - 1}.");
                    }
                    
                    var apiResponse = await message.HttpClient.GetAsync($"{message.ResourceUrl}?offset={offset}&limit={limit}{changeWindowParms}")
                        .ConfigureAwait(false);
                    
                    string json = await apiResponse.Content.ReadAsStringAsync()
                        .ConfigureAwait(false);

                    JArray items;
                    
                    try
                    {
                        items = JArray.Parse(json);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"{message.ResourceUrl}: {ex}{Environment.NewLine}{json}");
                        break;
                    }

                    // Iterate through the returned items
                    foreach (var item in items.OfType<JObject>())
                    {
                        var actionMessage = message.CreateItemActionMessage(message, item);

                        // Stop processing individual items if cancellation has been requested
                        if (message.CancellationSource.IsCancellationRequested)
                        {
                            _logger.Debug($"{message.ResourceUrl}: Cancellation requested during item '{typeof(TItemActionMessage).Name}' creation.");
                            return Enumerable.Empty<TItemActionMessage>();
                        }
                        
                        // Add the item to the buffer for processing into the target API
                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug($"{message.ResourceUrl}: Adding individual action message of type '{typeof(TItemActionMessage).Name}' for item {item["id"].Value<string>()}...");
                        }

                        transformedMessages.Add(actionMessage);
                    }

                    if (message.IsFinalPage && items.Count == limit)
                    {
                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug($"{message.ResourceUrl}: Final page was full. Attempting to retrieve more data.");
                        }

                        // Looks like there could be more data
                        offset += limit;
                        continue;
                    }
                
                    break;
                } while (true);

                return transformedMessages;
            }
            catch (Exception ex)
            {
                _logger.Error($"{message.ResourceUrl}: {ex}");
                return new TItemActionMessage[0];
            }
        }
    }
}