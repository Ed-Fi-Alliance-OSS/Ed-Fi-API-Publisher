using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using EdFi.Tools.ApiPublisher.Core.Extensions;
using EdFi.Tools.ApiPublisher.Core.Helpers;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using log4net;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Blocks
{
    public static class StreamResource
    {
        private static readonly ILog _logger = LogManager.GetLogger(typeof(StreamResource));
        
        public static TransformManyBlock<StreamResourceMessage, StreamResourcePageMessage<TItemActionMessage>>
            CreateBlock<TItemActionMessage>(
                Func<StreamResourcePageMessage<TItemActionMessage>, JObject, TItemActionMessage> createItemActionMessage, 
                ITargetBlock<ErrorItemMessage> errorHandlingBlock)
        {
            return new TransformManyBlock<StreamResourceMessage, StreamResourcePageMessage<TItemActionMessage>>(
                async msg =>
                {
                    if (msg.CancellationSource.IsCancellationRequested)
                    {
                        _logger.Debug($"{msg.ResourceUrl}: Cancellation requested.");
                        return Enumerable.Empty<StreamResourcePageMessage<TItemActionMessage>>();
                    }

                    try
                    {
                        var messages = await DoStreamResource(msg, createItemActionMessage, errorHandlingBlock)
                            .ConfigureAwait(false);
                        
                        if (msg.CancellationSource.IsCancellationRequested)
                        {
                            _logger.Debug($"{msg.ResourceUrl}: Cancellation requested.");
                            return Enumerable.Empty<StreamResourcePageMessage<TItemActionMessage>>();
                        }

                        return messages;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"{msg.ResourceUrl}: An unhandled exception occurred in the StreamResource block: {ex}");
                        throw;
                    }
                });
        }

        private static async Task<IEnumerable<StreamResourcePageMessage<TItemActionMessage>>>
            DoStreamResource<TItemActionMessage>(StreamResourceMessage message,
                Func<StreamResourcePageMessage<TItemActionMessage>, JObject, TItemActionMessage> createItemActionMessage, 
                ITargetBlock<ErrorItemMessage> errorHandlingBlock)
        {
            if (message.Dependencies.Any())
            {
                if (_logger.IsDebugEnabled)
                    _logger.Debug($"{message.ResourceUrl}: Waiting for dependencies to complete before streaming.");

                // Wait for other resources to complete processing
                await Task.WhenAll(message.Dependencies)
                    .ConfigureAwait(false);
                
                if (_logger.IsDebugEnabled)
                    _logger.Debug($"{message.ResourceUrl}: Dependencies completed. Starting streaming of resources...");
            }

            try
            {
                if (message.ChangeWindow?.MaxChangeVersion != default(long)
                    && message.ChangeWindow?.MaxChangeVersion != null)
                {
                    _logger.Info($"{message.ResourceUrl}: Retrieving total count of items in change versions {message.ChangeWindow.MinChangeVersion} to {message.ChangeWindow.MaxChangeVersion}.");
                }
                else
                {
                    _logger.Info($"{message.ResourceUrl}: Retrieving total count of items.");
                }

                string changeWindowParms = RequestHelper.GetChangeWindowParms(message.ChangeWindow);
                var apiResponse = await message.HttpClient.GetAsync($"{message.ResourceUrl}?offset=0&limit=1&totalCount=true{changeWindowParms}")
                    .ConfigureAwait(false);

                if (!apiResponse.IsSuccessStatusCode)
                {
                    await HandleResourceCountRequestError<TItemActionMessage>(message, errorHandlingBlock, apiResponse);
                    
                    // Allow processing to continue with no additional work on this resource
                    return Enumerable.Empty<StreamResourcePageMessage<TItemActionMessage>>();
                }
                
                string totalCountHeaderValue = apiResponse.Headers.GetValues("total-count").FirstOrDefault();

                if (totalCountHeaderValue == null)
                {
                    string responseContent = await apiResponse.Content.ReadAsStringAsync();

                    // Publish an error for the resource to allow processing to continue, but to force failure.
                    _logger.Error($"{message.ResourceUrl}: Unable to obtain total count because Total-Count header was not returned by the source API -- skipping item processing. Response: {apiResponse.StatusCode}{Environment.NewLine}{responseContent}");
                    
                    errorHandlingBlock.Post(new ErrorItemMessage
                    {
                        ResourceUrl = message.ResourceUrl,
                        Method = HttpMethod.Get.ToString(),
                        ResponseStatus = apiResponse.StatusCode,
                        ResponseContent = responseContent,
                    });
                    
                    // Allow processing to continue without performing additional work on this resource.
                    return Enumerable.Empty<StreamResourcePageMessage<TItemActionMessage>>();
                }
                
                _logger.Info($"{message.ResourceUrl}: Total count = {totalCountHeaderValue}");

                long totalCount = long.Parse(totalCountHeaderValue);

                long offset = 0;
                int limit = message.PageSize;

                var pageMessages = new List<StreamResourcePageMessage<TItemActionMessage>>();
                
                while (offset < totalCount)
                {
                    var pageMessage = new StreamResourcePageMessage<TItemActionMessage>
                    {
                        HttpClient = message.HttpClient,
                        ResourceUrl = message.ResourceUrl, 
                        Limit = limit, 
                        Offset = offset,
                        ChangeWindow = message.ChangeWindow,
                        CreateItemActionMessage = createItemActionMessage,
                        CancellationSource = message.CancellationSource,
                        PostAuthorizationFailureRetry = message.PostAuthorizationFailureRetry,
                    };
                    
                    pageMessages.Add(pageMessage);

                    offset += limit;
                }

                // Flag the last page for special "continuation" processing
                if (pageMessages.Any())
                {
                    pageMessages.Last().IsFinalPage = true;
                }
                
                return pageMessages;
            }
            catch (Exception ex)
            {
                _logger.Error($"{message.ResourceUrl}: {ex}");
                return Enumerable.Empty<StreamResourcePageMessage<TItemActionMessage>>();
            }
        }

        private static async Task HandleResourceCountRequestError<TItemActionMessage>(StreamResourceMessage message,
            ITargetBlock<ErrorItemMessage> errorHandlingBlock, HttpResponseMessage apiResponse)
        {
            string responseContent = await apiResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

            // Was this an authorization failure?
            if (apiResponse.StatusCode == HttpStatusCode.Forbidden)
            {
                // Is this a descriptor resource?
                if (ResourcePathHelper.IsDescriptor(message.ResourceUrl))
                {
                    // Being denied read access to descriptors is potentially problematic, but is not considered
                    // to be breaking in its own right for change processing. We'll fail downstream
                    // POSTs if descriptors haven't been initialized correctly on the target.
                    _logger.Warn($"{message.ResourceUrl}: {apiResponse.StatusCode} - Unable to obtain total count for descriptor due to authorization failure. Descriptor values will not be published to the target, but processing will continue.{Environment.NewLine}Response content: {responseContent}");
                    return;
                }
            }

            _logger.Error($"{message.ResourceUrl}: {apiResponse.StatusCode} - Unable to obtain total count due to request failure. This resource will not be processed. Downstream failures are possible.{Environment.NewLine}Response content: {responseContent}");

            // Publish an error for the resource to allow processing to continue, but to force failure.
            errorHandlingBlock.Post(new ErrorItemMessage
            {
                ResourceUrl = message.ResourceUrl,
                Method = HttpMethod.Get.ToString(),
                ResponseStatus = apiResponse.StatusCode,
                ResponseContent = apiResponse.Content.ReadAsStringAsync().GetResultSafely(),
            });
        }
    }
}