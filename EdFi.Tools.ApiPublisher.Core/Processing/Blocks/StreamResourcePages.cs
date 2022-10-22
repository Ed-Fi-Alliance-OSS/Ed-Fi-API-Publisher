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
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using log4net;
using Newtonsoft.Json.Linq;
using Polly;
using Polly.Contrib.WaitAndRetry;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Blocks
{
    public static class StreamResourcePages
    {
        private static readonly ILog _logger = LogManager.GetLogger(typeof(StreamResourcePages));
        
        public static TransformManyBlock<StreamResourcePageMessage<TItemActionMessage>, TItemActionMessage> CreateBlock<TItemActionMessage>(
            Options options, 
            ITargetBlock<ErrorItemMessage> errorHandlingBlock)
        {
            var streamResourcePagesBlock =
                new TransformManyBlock<StreamResourcePageMessage<TItemActionMessage>, TItemActionMessage>(
                    async msg =>
                    {
                        try
                        {
                            return await HandleStreamResourcePage(msg, options, errorHandlingBlock).ConfigureAwait(false);
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
        
        // ======================================================
        // BEGIN POSSIBLE SEAM: Page data source (Ed-Fi ODS API) 
        // ======================================================
        private static async Task<IEnumerable<TItemActionMessage>> HandleStreamResourcePage<TItemActionMessage>(
            StreamResourcePageMessage<TItemActionMessage> message,
            Options options, 
            ITargetBlock<ErrorItemMessage> errorHandlingBlock)
        {
            long offset = message.Offset;
            int limit = message.Limit;

            string changeWindowQueryStringParameters = ApiRequestHelper.GetChangeWindowQueryStringParameters(message.ChangeWindow);
            
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

                    var delay = Backoff.ExponentialBackoff(
                        TimeSpan.FromMilliseconds(options.RetryStartingDelayMilliseconds),
                        options.MaxRetryAttempts);

                    int attempts = 0;
                    
                    var apiResponse = await Policy
                        .HandleResult<HttpResponseMessage>(r => r.StatusCode.IsPotentiallyTransientFailure())
                        .WaitAndRetryAsync(delay, (result, ts, retryAttempt, ctx) =>
                        {
                            _logger.Warn(
                                $"{message.ResourceUrl}: Retrying GET page items {offset} to {offset + limit - 1} from source failed with status '{result.Result.StatusCode}'. Retrying... (retry #{retryAttempt} of {options.MaxRetryAttempts} with {ts.TotalSeconds:N1}s delay)");
                        })
                        .ExecuteAsync((ctx, ct) =>
                        {
                            attempts++;

                            if (attempts > 1)
                            {
                                if (_logger.IsDebugEnabled)
                                {
                                    _logger.Debug($"{message.ResourceUrl}: GET page items {offset} to {offset + limit - 1} from source attempt #{attempts}.");
                                }
                            }
                            
                            // Possible seam for getting a page of data (here, using Ed-Fi ODS API w/ offset/limit paging strategy)
                            return message.EdFiApiClient.HttpClient.GetAsync($"{message.EdFiApiClient.DataManagementApiSegment}{message.ResourceUrl}?offset={offset}&limit={limit}{changeWindowQueryStringParameters}", ct);
                        }, new Context(), CancellationToken.None);
                    
                    // Detect null content and provide a better error message (which happens only during unit testing if mocked requests aren't properly defined)
                    if (apiResponse.Content == null)
                    {
                        throw new NullReferenceException($"Content of response for '{message.EdFiApiClient.HttpClient.BaseAddress}{message.EdFiApiClient.DataManagementApiSegment}{message.ResourceUrl}?offset={offset}&limit={limit}{changeWindowQueryStringParameters}' was null.");
                    }
                    
                    string responseContent = await apiResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                    
                    // Failure
                    if (!apiResponse.IsSuccessStatusCode)
                    {
                        var error = new ErrorItemMessage
                        {
                            Method = HttpMethod.Get.ToString(),
                            ResourceUrl = $"{message.EdFiApiClient.DataManagementApiSegment}{message.ResourceUrl}",
                            Id = null,
                            Body = null,
                            ResponseStatus = apiResponse.StatusCode,
                            ResponseContent = responseContent
                        };

                        // Publish the failure
                        errorHandlingBlock.Post(error);

                        break;
                    }

                    // Success
                    if (_logger.IsInfoEnabled && attempts > 1)
                    {
                        _logger.Info(
                            $"{message.ResourceUrl}: GET page items {offset} to {offset + limit - 1} attempt #{attempts} returned {apiResponse.StatusCode}.");
                    }

                    // -------------------------------------------------------------------------------------------------------
                    transformedMessages.AddRange(TransformDataPageToItemActions(responseContent, apiResponse.StatusCode, message, errorHandlingBlock));
                    // -------------------------------------------------------------------------------------------------------

                    if (message.IsFinalPage && JArray.Parse(responseContent).Count == limit)
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
                return Array.Empty<TItemActionMessage>();
            }
        }
        // ==========================================================
        // END POSSIBLE SEAM: Page data source (using Ed-Fi ODS API) 
        // ==========================================================

        // ======================================================================================
        // BEGIN POSSIBLE SEAM: Handle page of raw JSON content, return "Item Action Messages"
        // ======================================================================================
        // This implementation parses the JSON content and creates "ItemActionMessage" instances with a JsonObject for each resource item
        private static IEnumerable<TItemActionMessage> TransformDataPageToItemActions<TItemActionMessage>(
            string responseContent,
            HttpStatusCode responseStatusCode,
            StreamResourcePageMessage<TItemActionMessage> pageMessage,
            ITargetBlock<ErrorItemMessage> errorHandlingBlock)
        {
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
                    ResourceUrl = $"{pageMessage.EdFiApiClient.DataManagementApiSegment}{pageMessage.ResourceUrl}",
                    Id = null,
                    Body = null,
                    ResponseStatus = responseStatusCode,
                    ResponseContent = responseContent
                };

                // Publish the failure
                errorHandlingBlock.Post(error);
                
                _logger.Error($"{pageMessage.ResourceUrl}: JSON parsing of source page data failed: {ex}{Environment.NewLine}{responseContent}");

                throw new Exception("JSON parsing of source page data failed.", ex);
            }

            // Iterate through the returned items
            foreach (var item in items.OfType<JObject>())
            {
                var actionMessage = pageMessage.CreateItemActionMessage(pageMessage, item);

                // Stop processing individual items if cancellation has been requested
                if (pageMessage.CancellationSource.IsCancellationRequested)
                {
                    _logger.Debug($"{pageMessage.ResourceUrl}: Cancellation requested during item '{typeof(TItemActionMessage).Name}' creation.");

                    yield break;
                }

                // Add the item to the buffer for processing into the target API
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"{pageMessage.ResourceUrl}: Adding individual action message of type '{typeof(TItemActionMessage).Name}' for item {item["id"].Value<string>()}...");
                }

                yield return actionMessage;
            }
        }
        // ======================================================
        // END POSSIBLE SEAM: Handle page of raw JSON content
        // ======================================================
    }
}