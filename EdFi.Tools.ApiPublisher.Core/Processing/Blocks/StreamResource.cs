using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Extensions;
using EdFi.Tools.ApiPublisher.Core.Helpers;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using Newtonsoft.Json.Linq;
using Polly;
using Polly.Contrib.WaitAndRetry;
using Serilog;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Blocks
{
    public static class StreamResource
    {
        private static readonly ILogger _logger = Log.Logger.ForContext(typeof(StreamResource));
        
        public static TransformManyBlock<StreamResourceMessage, StreamResourcePageMessage<TItemActionMessage>>
            CreateBlock<TItemActionMessage>(
                Func<StreamResourcePageMessage<TItemActionMessage>, JObject, TItemActionMessage> createItemActionMessage,
                ITargetBlock<ErrorItemMessage> errorHandlingBlock,
                Options options,
                CancellationToken cancellationToken)
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
                        var messages = await DoStreamResource(msg, createItemActionMessage, errorHandlingBlock, options, cancellationToken)
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
            DoStreamResource<TItemActionMessage>(
                StreamResourceMessage message,
                Func<StreamResourcePageMessage<TItemActionMessage>, JObject, TItemActionMessage> createItemActionMessage,
                ITargetBlock<ErrorItemMessage> errorHandlingBlock,
                Options options,
                CancellationToken cancellationToken)
        {
            if (message.Dependencies.Any())
            {
                if (_logger.IsEnabled(LogEventLevel.Debug))
                    _logger.Debug($"{message.ResourceUrl}: Waiting for dependencies to complete before streaming...");

                // Wait for other resources to complete processing
                await Task.WhenAll(message.Dependencies)
                    .ConfigureAwait(false);

                if (_logger.IsEnabled(LogEventLevel.Debug))
                    _logger.Debug($"{message.ResourceUrl}: Dependencies completed. Waiting for an available processing slot...");
            }
            else
            {
                if (_logger.IsEnabled(LogEventLevel.Debug))
                    _logger.Debug($"{message.ResourceUrl}: Resource has no dependencies. Waiting for an available processing slot...");
            }

            // Wait for an available processing slot
            await message.ProcessingSemaphore.WaitAsync(cancellationToken);

            if (_logger.IsEnabled(LogEventLevel.Debug))
                _logger.Debug($"{message.ResourceUrl}: Processing slot acquired ({message.ProcessingSemaphore.CurrentCount} remaining). Starting streaming of resources...");

            try
            {
                if (message.ChangeWindow?.MaxChangeVersion != default(long) && message.ChangeWindow?.MaxChangeVersion != null)
                {
                    _logger.Information(
                        $"{message.ResourceUrl}: Retrieving total count of items in change versions {message.ChangeWindow.MinChangeVersion} to {message.ChangeWindow.MaxChangeVersion}.");
                }
                else
                {
                    _logger.Information($"{message.ResourceUrl}: Retrieving total count of items.");
                }

                string changeWindowParms = RequestHelper.GetChangeWindowParms(message.ChangeWindow);

                var delay = Backoff.ExponentialBackoff(
                    TimeSpan.FromMilliseconds(options.RetryStartingDelayMilliseconds),
                    options.MaxRetryAttempts);

                int attempt = 0;

                var apiResponse = await Policy
                    .HandleResult<HttpResponseMessage>(r => r.StatusCode.IsPotentiallyTransientFailure())
                    .WaitAndRetryAsync(delay, (result, ts, retryAttempt, ctx) =>
                        {
                            _logger.Warning(
                                $"{message.ResourceUrl}: Getting item count from source failed with status '{result.Result.StatusCode}'. Retrying... (retry #{retryAttempt} of {options.MaxRetryAttempts} with {ts.TotalSeconds:N1}s delay)");
                        })
                    .ExecuteAsync((ctx, ct) =>
                        {
                            attempt++;
                        
                            if (_logger.IsEnabled(LogEventLevel.Debug))
                            {
                                _logger.Debug($"{message.ResourceUrl}): Getting item count from source (attempt #{attempt})...");
                            }

                            return message.EdFiApiClient.HttpClient.GetAsync(
                                $"{message.EdFiApiClient.DataManagementApiSegment}{message.ResourceUrl}?offset=0&limit=1&totalCount=true{changeWindowParms}",
                                ct);
                        }, new Context(), cancellationToken);

                string responseContent = null;

                if (!apiResponse.IsSuccessStatusCode)
                {
                    _logger.Error(
                        $"{message.ResourceUrl}: Count request returned {apiResponse.StatusCode}{Environment.NewLine}{responseContent}");

                    await HandleResourceCountRequestErrorAsync<TItemActionMessage>(message, errorHandlingBlock, apiResponse)
                        .ConfigureAwait(false);

                    // Allow processing to continue with no additional work on this resource
                    return Enumerable.Empty<StreamResourcePageMessage<TItemActionMessage>>();
                }

                // Try to get the count header from the response
                if (!apiResponse.Headers.TryGetValues("total-count", out IEnumerable<string> headerValues))
                {
                    _logger.Warning(
                        $"{message.ResourceUrl}: Unable to obtain total count because Total-Count header was not returned by the source API -- skipping item processing, but overall processing will fail.");

                    // Publish an error for the resource. Feature is not supported.
                    await HandleResourceCountRequestErrorAsync<TItemActionMessage>(message, errorHandlingBlock, apiResponse)
                        .ConfigureAwait(false);

                    // Allow processing to continue as best it can with no additional work on this resource
                    return Enumerable.Empty<StreamResourcePageMessage<TItemActionMessage>>();
                }

                string totalCountHeaderValue = headerValues.First();
                
                _logger.Debug($"{message.ResourceUrl}: Total count header value = {totalCountHeaderValue}");

                long totalCount;

                try
                {
                    totalCount = long.Parse(totalCountHeaderValue);
                }
                catch (Exception)
                {
                    // Publish an error for the resource to allow processing to continue, but to force failure.
                    _logger.Error(
                        $"{message.ResourceUrl}: Unable to convert Total-Count header value of '{totalCountHeaderValue}'  returned by the source API to an integer.");

                    errorHandlingBlock.Post(
                        new ErrorItemMessage
                        {
                            ResourceUrl = $"{message.EdFiApiClient.DataManagementApiSegment}{message.ResourceUrl}",
                            Method = HttpMethod.Get.ToString(),
                            ResponseStatus = apiResponse.StatusCode,
                            ResponseContent = $"Total-Count: {totalCountHeaderValue}",
                        });

                    // Allow processing to continue without performing additional work on this resource.
                    return Enumerable.Empty<StreamResourcePageMessage<TItemActionMessage>>();
                }

                _logger.Information($"{message.ResourceUrl}: Total count = {totalCount}");

                long offset = 0;
                int limit = message.PageSize;

                var pageMessages = new List<StreamResourcePageMessage<TItemActionMessage>>();

                while (offset < totalCount)
                {
                    var pageMessage = new StreamResourcePageMessage<TItemActionMessage>
                    {
                        EdFiApiClient = message.EdFiApiClient,
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

        private static async Task HandleResourceCountRequestErrorAsync<TItemActionMessage>(StreamResourceMessage message,
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
                    _logger.Warning($"{message.ResourceUrl}: {apiResponse.StatusCode} - Unable to obtain total count for descriptor due to authorization failure. Descriptor values will not be published to the target, but processing will continue.{Environment.NewLine}Response content: {responseContent}");
                    return;
                }
            }

            _logger.Error($"{message.ResourceUrl}: {apiResponse.StatusCode} - Unable to obtain total count due to request failure. This resource will not be processed. Downstream failures are possible.{Environment.NewLine}Response content: {responseContent}");

            // Publish an error for the resource to allow processing to continue, but to force failure.
            errorHandlingBlock.Post(new ErrorItemMessage
            {
                ResourceUrl = $"{message.EdFiApiClient.DataManagementApiSegment}{message.ResourceUrl}",
                Method = HttpMethod.Get.ToString(),
                ResponseStatus = apiResponse.StatusCode,
                ResponseContent = await apiResponse.Content.ReadAsStringAsync().ConfigureAwait(false),
            });
        }
    }
}