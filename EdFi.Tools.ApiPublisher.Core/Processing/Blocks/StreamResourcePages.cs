using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
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
        
        public static TransformManyBlock<StreamResourcePageMessage<TItemActionMessage>, TItemActionMessage> GetBlock<TItemActionMessage>(
            Options options, 
            ITargetBlock<ErrorItemMessage> errorHandlingBlock)
        {
            var streamResourcePagesBlock =
                new TransformManyBlock<StreamResourcePageMessage<TItemActionMessage>, TItemActionMessage>(
                    async msg =>
                    {
                        try
                        {
                            return await StreamResourcePage(msg, options, errorHandlingBlock).ConfigureAwait(false);
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
            StreamResourcePageMessage<TItemActionMessage> message,
            Options options, 
            ITargetBlock<ErrorItemMessage> errorHandlingBlock)
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

                    int attempts = 0;
                    int delay = options.RetryStartingDelayMilliseconds;

                    HttpResponseMessage apiResponse = null;
                    string responseContent = null;
                    
                    while (attempts++ < options.MaxRetryAttempts)
                    {
                        try
                        {
                            if (attempts > 1)
                            {
                                if (_logger.IsDebugEnabled)
                                {
                                    _logger.Debug($"{message.ResourceUrl}: GET page items {offset} to {offset + limit - 1} from source attempt #{attempts}.");
                                }
                            }

                            apiResponse = await message.HttpClient.GetAsync($"{message.ResourceUrl}?offset={offset}&limit={limit}{changeWindowParms}")
                                .ConfigureAwait(false);
                    
                            responseContent = await apiResponse.Content.ReadAsStringAsync()
                                .ConfigureAwait(false);
                            
                            if (!apiResponse.IsSuccessStatusCode)
                            {
                                // Retry certain error types
                                if (apiResponse.StatusCode == HttpStatusCode.InternalServerError)
                                {
                                    _logger.Warn(
                                        $"{message.ResourceUrl}: Retrying GET page items {offset} to {offset + limit - 1} from source (attempt #{attempts} failed with status '{apiResponse.StatusCode}').");

                                    ExponentialBackOffHelper.PerformDelay(ref delay);
                                    continue;
                                }
                                
                                var error = new ErrorItemMessage
                                {
                                    Method = HttpMethod.Get.ToString(),
                                    ResourceUrl = message.ResourceUrl,
                                    Id = null,
                                    Body = null,
                                    ResponseStatus = apiResponse.StatusCode,
                                    ResponseContent = responseContent
                                };

                                // Publish the failure
                                errorHandlingBlock.Post(error);
                            }
                            
                            break;
                        }
                        catch (Exception ex)
                        {
                            _logger.Error($"{message.ResourceUrl}: GET page items {offset} to {offset + limit - 1} attempt #{attempts}: {ex}");

                            ExponentialBackOffHelper.PerformDelay(ref delay);
                        }
                    }

                    if (!apiResponse.IsSuccessStatusCode)
                    {
                        break;
                    }

                    // Success
                    if (_logger.IsInfoEnabled && attempts > 1)
                    {
                        _logger.Info(
                            $"{message.ResourceUrl}: GET page items {offset} to {offset + limit - 1} attempt #{attempts} returned {apiResponse.StatusCode}.");
                    }

                    JArray items;
                    
                    try
                    {
                        items = JArray.Parse(responseContent);
                    }
                    catch (Exception ex)
                    {
                        var error = new ErrorItemMessage
                        {
                            Method = HttpMethod.Get.ToString(),
                            ResourceUrl = message.ResourceUrl,
                            Id = null,
                            Body = null,
                            ResponseStatus = apiResponse.StatusCode,
                            ResponseContent = responseContent
                        };

                        // Publish the failure
                        errorHandlingBlock.Post(error);
                        
                        _logger.Error($"{message.ResourceUrl}: JSON parsing of source page data failed: {ex}{Environment.NewLine}{responseContent}");
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