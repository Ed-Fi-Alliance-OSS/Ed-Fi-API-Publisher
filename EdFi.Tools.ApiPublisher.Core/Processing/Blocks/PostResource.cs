using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks.Dataflow;
using EdFi.Tools.ApiPublisher.Core.ApiClientManagement;
using Newtonsoft.Json.Linq;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Extensions;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using log4net;
using Polly;
using Polly.Contrib.WaitAndRetry;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Blocks
{
    public static class PostResource
    {
        private static readonly ILog _logger = LogManager.GetLogger(typeof(PostResource));
        
        public static ValueTuple<ITargetBlock<PostItemMessage>, ISourceBlock<ErrorItemMessage>> CreateBlocks(
            EdFiApiClient targetApiClient,
            Options options,
            ITargetBlock<ErrorItemMessage> errorHandlingBlock)
        {
            var ignoredResourceByUrl = new ConcurrentDictionary<string, bool>();
            
            var postResourceBlock = new TransformManyBlock<PostItemMessage, ErrorItemMessage>(
                async msg =>
            {
                if (ignoredResourceByUrl.ContainsKey(msg.ResourceUrl))
                {
                    return Enumerable.Empty<ErrorItemMessage>();
                }
                
                var idToken = msg.Item["id"];
                string id = idToken.Value<string>();
                
                try
                {
                    if (_logger.IsDebugEnabled)
                    {
                        _logger.Debug($"{msg.ResourceUrl} (source id: {id}): Processing PostItemMessage (with up to {options.MaxRetryAttempts} retries).");
                    }
                
                    // Remove attributes not usable between API instances
                    msg.Item.Remove("id");
                    msg.Item.Remove("_etag");

                    // For descriptors, also strip the surrogate id
                    if (msg.ResourceUrl.EndsWith("Descriptors"))
                    {
                        string descriptorBaseName = msg.ResourceUrl.Split('/').Last().TrimEnd('s');
                        string descriptorIdPropertyName = $"{descriptorBaseName}Id";

                        msg.Item.Remove(descriptorIdPropertyName);
                    }
                    
                    var delay = Backoff.ExponentialBackoff(
                        TimeSpan.FromMilliseconds(options.RetryStartingDelayMilliseconds),
                        options.MaxRetryAttempts);

                    int attempt = 0;

                    var apiResponse = await Policy
                        .Handle<Exception>()
                        .OrResult<HttpResponseMessage>(r => 
                            // Descriptor Conflicts are not to be retried
                            (r.StatusCode == HttpStatusCode.Conflict && !msg.ResourceUrl.EndsWith("Descriptors", StringComparison.OrdinalIgnoreCase)) 
                            || !r.StatusCode.IsPermanentFailure())
                        .WaitAndRetryAsync(delay, async (result, ts, retryAttempt, ctx) =>
                        {
                            if (result.Exception != null)
                            {
                                _logger.Error($"{msg.ResourceUrl} (source id: {id}, attempt #{attempt}): {result.Exception}");
                            }
                            else
                            {
                                string responseContent = await result.Result.Content.ReadAsStringAsync().ConfigureAwait(false);

                                _logger.Warn($"{msg.ResourceUrl} (source id: {id}): Posting resource failed with status '{result.Result.StatusCode}'. Retrying... (retry #{retryAttempt} of {options.MaxRetryAttempts} with {ts.TotalSeconds:N1}s delay):{Environment.NewLine}{responseContent}");
                            }
                        })
                        .ExecuteAsync((ctx, ct) =>
                        {
                            attempt++;

                            if (_logger.IsDebugEnabled)
                            {
                                if (attempt > 1)
                                {
                                    _logger.Debug($"{msg.ResourceUrl} (source id: {id}): POST attempt #{attempt}.");
                                }
                                else
                                {
                                    _logger.Debug($"{msg.ResourceUrl} (source id: {id}): Sending POST request.");
                                }
                            }

                            return targetApiClient.HttpClient.PostAsync(
                                msg.ResourceUrl,
                                new StringContent(msg.Item.ToString(), Encoding.UTF8, "application/json"),
                                ct);
                            
                        }, new Context(), CancellationToken.None);

                    // Failure
                    if (!apiResponse.IsSuccessStatusCode)
                    {
                        // Descriptor POSTs behave slightly different than other resources in
                        // that the DescriptorId must be used to update the descriptor value,
                        // while a POST with the values will result in a 409 Conflict if the values
                        // already exist. Thus, a Conflict response can be safely ignored as it
                        // indicates the data is already present and nothing more needs to be done.
                        if (msg.ResourceUrl.EndsWith("Descriptors") &&
                            apiResponse.StatusCode == HttpStatusCode.Conflict)
                        {
                            return Enumerable.Empty<ErrorItemMessage>();
                        }
                        
                        // Gracefully handle authorization errors by using the retry action delegate
                        // (if present) to post the message to the retry "resource" queue 
                        if (apiResponse.StatusCode == HttpStatusCode.Forbidden)
                        {
                            // Determine if current resource has an authorization retry queue
                            if (msg.PostAuthorizationFailureRetry != null)
                            {
                                if (_logger.IsDebugEnabled)
                                {
                                    _logger.Debug($"{msg.ResourceUrl} (source id: {id}): Authorization failed -- deferring for retry after pertinent associations are processed.");
                                }
                                
                                // Re-add the identifier, and pass the message along to the "retry" resource (after associations have been processed)
                                msg.Item.Add("id", idToken);
                                msg.PostAuthorizationFailureRetry(msg);

                                // Deferring for retry - no errors to publish
                                return Enumerable.Empty<ErrorItemMessage>();
                            }
                        }
                        
                        string responseContent = await apiResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                        
                        // If the failure is Forbidden, and we should treat it as a warning
                        if (apiResponse.StatusCode == HttpStatusCode.Forbidden
                            && msg.PostAuthorizationFailureRetry == null
                            && targetApiClient.ConnectionDetails?.TreatForbiddenPostAsWarning == true)
                        {
                            // Warn and ignore all future data for this resource
                            _logger.Warn($"{msg.ResourceUrl} (source id: {id}): Authorization failed on POST of resource with no authorization failure handling defined. Remaining resource items will be ignored. Response status: {apiResponse.StatusCode}{Environment.NewLine}{responseContent}");
                            ignoredResourceByUrl.TryAdd(msg.ResourceUrl, true);
                            return Enumerable.Empty<ErrorItemMessage>();
                        }
                        
                        // Unhandled error, no retries to be attempted... surface the error in the log
                        _logger.Error($"{msg.ResourceUrl} (source id: {id}): POST returned {apiResponse.StatusCode}{Environment.NewLine}{responseContent}");

                        // Publish the failed data
                        var error = new ErrorItemMessage
                        {
                            Method = HttpMethod.Post.ToString(),
                            ResourceUrl = msg.ResourceUrl,
                            Id = id,
                            Body = msg.Item,
                            ResponseStatus = apiResponse.StatusCode,
                            ResponseContent = responseContent
                        };

                        return new[] {error};
                    }
                        
                    // Success
                    if (_logger.IsInfoEnabled && attempt > 1)
                    {
                        _logger.Info(
                            $"{msg.ResourceUrl} (source id: {id}): POST attempt #{attempt} returned {apiResponse.StatusCode}.");
                    }

                    if (_logger.IsDebugEnabled)
                    {
                        _logger.Debug(
                            $"{msg.ResourceUrl} (source id: {id}): POST returned {apiResponse.StatusCode}");
                    }

                    // Success - no errors to publish
                    return Enumerable.Empty<ErrorItemMessage>();
                }
                catch (Exception ex)
                {
                    _logger.Error($"{msg.ResourceUrl} (source id: {id}): An unhandled exception occurred in the PostResource block: {ex}");
                    throw;
                }
            }, new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = options.MaxDegreeOfParallelismForPostResourceItem
            });
            
            return (postResourceBlock, postResourceBlock);
        }

        public static PostItemMessage CreateItemActionMessage(StreamResourcePageMessage<PostItemMessage> msg, JObject j)
        {
            return new PostItemMessage
            {
                Item = j,
                ResourceUrl = msg.ResourceUrl,
                PostAuthorizationFailureRetry = msg.PostAuthorizationFailureRetry,
            };
        }
    }
}