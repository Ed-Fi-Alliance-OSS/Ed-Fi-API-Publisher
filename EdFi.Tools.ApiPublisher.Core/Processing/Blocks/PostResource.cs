using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Newtonsoft.Json.Linq;
using EdFi.Tools.ApiPublisher.Core.Extensions;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using log4net;
using Newtonsoft.Json;
using Polly;
using Polly.Contrib.WaitAndRetry;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Blocks
{
    public static class PostResource
    {
        private static readonly ILog _logger = LogManager.GetLogger(typeof(PostResource));

        public static ValueTuple<ITargetBlock<PostItemMessage>, ISourceBlock<ErrorItemMessage>> CreateBlocks(CreateBlocksRequest createBlocksRequest)
        {
            var options = createBlocksRequest.Options;
            var targetEdFiApiClient = createBlocksRequest.TargetApiClient;
            var sourceEdFiApiClient = createBlocksRequest.SourceApiClient;
            
            var ignoredResourceByUrl = new ConcurrentDictionary<string, bool>();

            var missingDependencyByResourcePath = new Dictionary<string, string>();

            var items = createBlocksRequest.AuthorizationFailureHandling.SelectMany(
                h => h.UpdatePrerequisitePaths.Select(
                    prereq => new
                    {
                        ResourcePath = prereq,
                        DependencyResourcePath = h.Path
                    }));

            foreach (var item in items)
            {
                missingDependencyByResourcePath.Add(item.ResourcePath, item.DependencyResourcePath);
            }

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

                    int attempts = 0;

                    var apiResponse = await Policy
                        .Handle<Exception>()
                        .OrResult<HttpResponseMessage>(r =>  
                            // Descriptor Conflicts are not to be retried
                            (r.StatusCode == HttpStatusCode.Conflict && !msg.ResourceUrl.EndsWith("Descriptors", StringComparison.OrdinalIgnoreCase)) 
                            || r.StatusCode.IsPotentiallyTransientFailure()
                            || IsBadRequestForUnresolvedReferenceOfPrimaryRelationship(r, msg))
                        .WaitAndRetryAsync(delay, async (result, ts, retryAttempt, ctx) =>
                        {
                            if (result.Exception != null)
                            {
                                _logger.Error($"{msg.ResourceUrl} (source id: {id}): POST attempt #{attempts} failed with an exception. Retrying... (retry #{retryAttempt} of {options.MaxRetryAttempts} with {ts.TotalSeconds:N1}s delay):{Environment.NewLine}{result.Exception}");
                            }
                            else
                            {
                                string responseContent = await result.Result.Content.ReadAsStringAsync().ConfigureAwait(false);

                                _logger.Warn($"{msg.ResourceUrl} (source id: {id}): POST attempt #{attempts} failed with status '{result.Result.StatusCode}'. Retrying... (retry #{retryAttempt} of {options.MaxRetryAttempts} with {ts.TotalSeconds:N1}s delay):{Environment.NewLine}{responseContent}");
                            }
                        })
                        .ExecuteAsync(async (ctx, ct) =>
                        {
                            attempts++;

                            if (_logger.IsDebugEnabled)
                            {
                                if (attempts > 1)
                                {
                                    _logger.Debug($"{msg.ResourceUrl} (source id: {id}): POST attempt #{attempts}.");
                                }
                                else
                                {
                                    _logger.Debug($"{msg.ResourceUrl} (source id: {id}): Sending POST request.");
                                }
                            }

                            var response = await targetEdFiApiClient.HttpClient.PostAsync(
                                $"{targetEdFiApiClient.DataManagementApiSegment}{msg.ResourceUrl}",
                                new StringContent(msg.Item.ToString(), Encoding.UTF8, "application/json"),
                                ct);

                            await HandleMissingDependencyAsync(response, msg);

                            return response;
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
                            if (_logger.IsDebugEnabled)
                            {
                                _logger.Debug(
                                    $"{msg.ResourceUrl} (source id: {id}): POST returned {HttpStatusCode.Conflict}, but for descriptors this means the value is already present.");
                            }
                            
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
                            && targetEdFiApiClient.ConnectionDetails?.TreatForbiddenPostAsWarning == true)
                        {
                            // Warn and ignore all future data for this resource
                            _logger.Warn($"{msg.ResourceUrl} (source id: {id}): Authorization failed on POST of resource with no authorization failure handling defined. Remaining resource items will be ignored. Response status: {apiResponse.StatusCode}{Environment.NewLine}{responseContent}");
                            ignoredResourceByUrl.TryAdd(msg.ResourceUrl, true);
                            return Enumerable.Empty<ErrorItemMessage>();
                        }
                        
                        // Error is final, log it and indicate failure for processing
                        _logger.Error($"{msg.ResourceUrl} (source id: {id}): POST attempt #{attempts} failed with status '{apiResponse.StatusCode}':{Environment.NewLine}{responseContent}");

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
                    if (attempts > 1)
                    {
                        if (_logger.IsInfoEnabled)
                        {
                            _logger.Info(
                                $"{msg.ResourceUrl} (source id: {id}): POST attempt #{attempts} returned {apiResponse.StatusCode}.");
                        }
                    }
                    else
                    {
                        // Ensure a log entry when POST succeeds on first attempt and DEBUG logging is enabled
                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug(
                                $"{msg.ResourceUrl} (source id: {id}): POST attempt #{attempts} returned {apiResponse.StatusCode}.");
                        }
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

            async Task<string> GetResponseMessageTextAsync(HttpResponseMessage response)
            {
                try
                {
                    var responseMessageToken = JObject.Parse(await response.Content.ReadAsStringAsync());

                    // If the failure message is related to a missing reference ("reference cannot be resolve")
                    return responseMessageToken["message"]?.Value<string>();
                }
                catch (Exception)
                {
                    // Unable to parse response, unable to automatically recover
                    return null;
                }
            }

            string GetResponseMessageText(HttpResponseMessage response)
            {
                try
                {
                    string content = response.Content.ReadAsStringAsync().ConfigureAwait(false).GetAwaiter().GetResult();
                    var responseMessageToken = JObject.Parse(content);

                    // If the failure message is related to a missing reference ("reference cannot be resolve")
                    return responseMessageToken["message"]?.Value<string>();
                }
                catch (Exception)
                {
                    // Unable to parse response, unable to automatically recover
                    return null;
                }
            }

            bool IsBadRequestForUnresolvedReferenceOfPrimaryRelationship(HttpResponseMessage postItemResponse, PostItemMessage msg)
            {
                // If response is a Bad Request, check for need to explicitly fetch dependencies
                if (postItemResponse.StatusCode == HttpStatusCode.BadRequest)
                {
                    // If resource is a "primary relationship" configured in authorization failure handling
                    if (missingDependencyByResourcePath.TryGetValue(msg.ResourceUrl, out string missingDependencyResourcePath))
                    {
                        string responseMessageText = GetResponseMessageText(postItemResponse);

                        if (responseMessageText?.Contains("reference could not be resolved.") == true)
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
            
            async Task HandleMissingDependencyAsync(HttpResponseMessage postItemResponse, PostItemMessage msg)
            {
                // If response is a Bad Request, check for need to explicitly fetch dependencies
                if (postItemResponse.StatusCode == HttpStatusCode.BadRequest)
                {
                    // If resource is a "primary relationship" configured in authorization failure handling
                    if (missingDependencyByResourcePath.TryGetValue(msg.ResourceUrl, out string missingDependencyResourcePath))
                    {
                        string responseMessageText = await GetResponseMessageTextAsync(postItemResponse);

                        if (responseMessageText?.Contains("reference could not be resolved.") == true)
                        {
                            // Infer reference name from message. This is a bit fragile, but no other choice here.
                            var referenceNameMatch = Regex.Match(
                                responseMessageText,
                                @"(?<ReferencedResourceName>\w+) reference could not be resolved.");

                            if (referenceNameMatch.Success)
                            {
                                //----------------------------------------------------------------------------------------------
                                string referencedResourceName = referenceNameMatch.Groups["ReferencedResourceName"].Value;
                                string referenceName = referencedResourceName.ToCamelCase() + "Reference";

                                // Get the missing reference's source URL
                                string dependencyItemUrl = msg.Item.SelectToken($"{referenceName}.link.href")?.Value<string>();

                                _logger.Info(
                                    $"{msg.ResourceUrl}: Attempting to retrieve missing '{referencedResourceName}' reference based on 'authorizationFailureHandling' metadata in apiPublisherSettings.json.");

                                var getByIdDelay = Backoff.ExponentialBackoff(
                                    TimeSpan.FromMilliseconds(options.RetryStartingDelayMilliseconds),
                                    options.MaxRetryAttempts);

                                int getByIdAttempts = 0;

                                var getByIdResponse = await Policy
                                    .HandleResult<HttpResponseMessage>(r => r.StatusCode.IsPotentiallyTransientFailure())
                                    .WaitAndRetryAsync(
                                        getByIdDelay,
                                        (result, ts, retryAttempt, ctx) =>
                                        {
                                            _logger.Warn(
                                                $"{msg.ResourceUrl}: Retrying GET for missing '{referencedResourceName}' reference from source failed with status '{result.Result.StatusCode}'. Retrying... (retry #{retryAttempt} of {options.MaxRetryAttempts} with {ts.TotalSeconds:N1}s delay)");
                                        })
                                    .ExecuteAsync(
                                        (ctx, ct) =>
                                        {
                                            getByIdAttempts++;

                                            if (getByIdAttempts > 1)
                                            {
                                                if (_logger.IsDebugEnabled)
                                                {
                                                    _logger.Debug(
                                                        $"{msg.ResourceUrl}: GET for missing '{referencedResourceName}' reference from source attempt #{getByIdAttempts}.");
                                                }
                                            }

                                            return sourceEdFiApiClient.HttpClient.GetAsync(
                                                $"{sourceEdFiApiClient.DataManagementApiSegment}{dependencyItemUrl}",
                                                ct);
                                        },
                                        new Context(),
                                        CancellationToken.None);

                                // Detect null content and provide a better error message (which happens only during unit testing if mocked requests aren't properly defined)
                                if (getByIdResponse.Content == null)
                                {
                                    throw new NullReferenceException(
                                        $"Content of response for '{sourceEdFiApiClient.HttpClient.BaseAddress}{sourceEdFiApiClient.DataManagementApiSegment}{dependencyItemUrl}' was null.");
                                }

                                // Did we successfully retrieve the missing dependency?
                                if (getByIdResponse.StatusCode == HttpStatusCode.OK)
                                {
                                    string missingItemJson = await getByIdResponse.Content.ReadAsStringAsync();
                                    var missingItem = JObject.Parse(missingItemJson);

                                    // Clean up the JObject for POSTing against the target
                                    // Remove attributes not usable between API instances
                                    missingItem.Remove("id");
                                    missingItem.Remove("_etag");

                                    var missingItemDelay = Backoff.ExponentialBackoff(
                                        TimeSpan.FromMilliseconds(options.RetryStartingDelayMilliseconds),
                                        options.MaxRetryAttempts);

                                    if (_logger.IsDebugEnabled)
                                    {
                                        _logger.Debug(
                                            $"{msg.ResourceUrl}: Attempting to POST missing '{referencedResourceName}' reference to the target.");
                                    }

                                    // Post the resource to target now
                                    var missingItemPostResponse = await Policy
                                        .HandleResult<HttpResponseMessage>(r => r.StatusCode.IsPotentiallyTransientFailure())
                                        .WaitAndRetryAsync(
                                            missingItemDelay,
                                            (result, ts, retryAttempt, ctx) =>
                                            {
                                                _logger.Warn(
                                                    $"{msg.ResourceUrl}: Retrying POST for missing '{referencedResourceName}' reference against target failed with status '{result.Result.StatusCode}'. Retrying... (retry #{retryAttempt} of {options.MaxRetryAttempts} with {ts.TotalSeconds:N1}s delay)");
                                            })
                                        .ExecuteAsync(
                                            (ctx, ct) =>
                                            {
                                                getByIdAttempts++;

                                                if (getByIdAttempts > 1)
                                                {
                                                    if (_logger.IsDebugEnabled)
                                                    {
                                                        _logger.Debug(
                                                            $"{msg.ResourceUrl}: GET for missing '{referencedResourceName}' reference from source attempt #{getByIdAttempts}.");
                                                    }
                                                }

                                                return targetEdFiApiClient.HttpClient.PostAsync(
                                                    $"{targetEdFiApiClient.DataManagementApiSegment}{missingDependencyResourcePath}",
                                                    new StringContent(missingItem.ToString(Formatting.None), Encoding.UTF8, "application/json"),
                                                    ct);
                                            },
                                            new Context(),
                                            CancellationToken.None);

                                    if (!missingItemPostResponse.IsSuccessStatusCode)
                                    {
                                        string responseContent = await getByIdResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

                                        _logger.Error(
                                            $"{msg.ResourceUrl}: POST of missing '{referencedResourceName}' reference to the target returned status '{missingItemPostResponse.StatusCode}': {responseContent}.");
                                    }
                                    else
                                    {
                                        _logger.Info(
                                            $"{msg.ResourceUrl}: POST of missing '{referencedResourceName}' reference to the target returned status '{missingItemPostResponse.StatusCode}'.");
                                    }
                                }
                                else
                                {
                                    string responseContent = await getByIdResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                                    
                                    _logger.Error($"{msg.ResourceUrl}: GET by Id request from source API for missing '{referencedResourceName}' reference failed with status '{getByIdResponse.StatusCode}': {responseContent}");
                                }
                                //----------------------------------------------------------------------------------------------
                            }
                        }
                    }
                }
            }
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