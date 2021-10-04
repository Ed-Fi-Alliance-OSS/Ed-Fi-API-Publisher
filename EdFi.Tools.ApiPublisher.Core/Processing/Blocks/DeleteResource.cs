using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks.Dataflow;
using EdFi.Tools.ApiPublisher.Core.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Extensions;
using EdFi.Tools.ApiPublisher.Core.Helpers;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using log4net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Blocks
{
    public static class DeleteResource
    {
        private static readonly ILog _logger = LogManager.GetLogger(typeof(DeleteResource));
        
        public static ValueTuple<ITargetBlock<GetItemForDeletionMessage>, ISourceBlock<ErrorItemMessage>> CreateBlocks(
            EdFiApiClient targetApiClient, Options options,
            ITargetBlock<ErrorItemMessage> errorHandlingBlock)
        {
            var getItemForDeletionBlock =
                CreateGetItemForDeletionBlock(targetApiClient.HttpClient, options, errorHandlingBlock);

            var deleteResourceBlock = CreateDeleteResourceBlock(targetApiClient.HttpClient, options);

            getItemForDeletionBlock.LinkTo(deleteResourceBlock, new DataflowLinkOptions {PropagateCompletion = true});
            
            return
                ((ITargetBlock<GetItemForDeletionMessage>) getItemForDeletionBlock, 
                (ISourceBlock<ErrorItemMessage>) deleteResourceBlock);
        }

        private static TransformManyBlock<GetItemForDeletionMessage, DeleteItemMessage> CreateGetItemForDeletionBlock(
            HttpClient targetHttpClient, 
            Options options, 
            ITargetBlock<ErrorItemMessage> errorHandlingBlock)
        {
            var getItemForDeletionBlock = new TransformManyBlock<GetItemForDeletionMessage, DeleteItemMessage>(
                async msg =>
                {
                    // If the message wasn't created (because there is no natural key information)
                    if (msg == null)
                    {
                        return Enumerable.Empty<DeleteItemMessage>();
                    }
                    
                    string id = msg.Id;

                    try
                    {
                        var keyValueParms = msg.KeyValues
                            .OfType<JProperty>()
                            .Select(p => $"{p.Name}={WebUtility.UrlEncode(GetQueryStringValue(p))}");

                        string queryString = String.Join("&", keyValueParms);
                    
                        int attempts = 0;
                        int maxAttempts = 1 + Math.Max(0, options.MaxRetryAttempts);
                        int delay = options.RetryStartingDelayMilliseconds;

                        HttpResponseMessage apiResponse = null;
                        string responseContent = null;

                        while (++attempts <= maxAttempts)
                        {
                            try
                            {
                                if (attempts > 1)
                                {
                                    if (_logger.IsDebugEnabled)
                                    {
                                        _logger.Debug($"{msg.ResourceUrl} (source id: {msg.Id}): GET by key on target attempt #{attempts} ({queryString}).");
                                    }
                                }

                                apiResponse = await targetHttpClient.GetAsync($"{msg.ResourceUrl}?{queryString}").ConfigureAwait(false);

                                responseContent = await apiResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

                                if (!apiResponse.IsSuccessStatusCode)
                                {
                                    _logger.Error(
                                        $"{msg.ResourceUrl} (source id: {id}): GET by key returned {apiResponse.StatusCode}{Environment.NewLine}{responseContent}");

                                    // Retry certain error types
                                    if (!apiResponse.StatusCode.IsPermanentFailure())
                                    {
                                        _logger.Warn(
                                            $"{msg.ResourceUrl} (source id: {id}): Retrying select by key on resource (attempt #{attempts} failed with status '{apiResponse.StatusCode}').");

                                        ExponentialBackOffHelper.PerformDelay(ref delay);
                                        continue;
                                    }
                            
                                    var error = new ErrorItemMessage
                                    {
                                        Method = HttpMethod.Get.ToString(),
                                        ResourceUrl = msg.ResourceUrl,
                                        Id = id,
                                        Body = null,
                                        ResponseStatus = apiResponse.StatusCode,
                                        ResponseContent = responseContent
                                    };

                                    // Publish the failure
                                    errorHandlingBlock.Post(error);
                                
                                    // No delete to process
                                    return Enumerable.Empty<DeleteItemMessage>();
                                }

                                // Success
                                if (_logger.IsInfoEnabled && attempts > 1)
                                {
                                    _logger.Info(
                                        $"{msg.ResourceUrl} (source id: {id}): GET by key attempt #{attempts} returned {apiResponse.StatusCode}.");
                                }

                                if (_logger.IsDebugEnabled)
                                {
                                    _logger.Debug($"{msg.ResourceUrl} (source id: {id}): GET by key returned {apiResponse.StatusCode}");
                                }

                                var getByKeyResults = JArray.Parse(responseContent);

                                // If the item to be deleted cannot be found...
                                if (getByKeyResults.Count == 0)
                                {
                                    if (_logger.IsDebugEnabled)
                                    {
                                        _logger.Debug($"{msg.ResourceUrl} (source id: {msg.Id}): GET by key for deletion returned no results on target API ({queryString}).");
                                    }
                                    
                                    return Enumerable.Empty<DeleteItemMessage>();
                                }

                                return new[]
                                {
                                    new DeleteItemMessage
                                    {
                                        ResourceUrl = msg.ResourceUrl,
                                        Id = getByKeyResults[0]["id"].Value<string>(),
                                        SourceId = msg.Id,
                                    }
                                };
                            }
                            catch (Exception ex)
                            {
                                _logger.Error($"{msg.ResourceUrl} (source id: {id}): GET by key attempt #{attempts}): {ex}");

                                ExponentialBackOffHelper.PerformDelay(ref delay);
                            }
                        }

                        // If retry count exceeded with a failure response, publish the failure
                        if (attempts > maxAttempts && apiResponse?.IsSuccessStatusCode == false)
                        {
                            var error = new ErrorItemMessage
                            {
                                Method = HttpMethod.Get.ToString(),
                                ResourceUrl = $"{msg.ResourceUrl}?{queryString}",
                                Id = id,
                                Body = null,
                                ResponseStatus = apiResponse?.StatusCode,
                                ResponseContent = responseContent
                            };

                            // Publish the failure
                            errorHandlingBlock.Post(error);
                        }

                        // Success - no errors to publish
                        return Enumerable.Empty<DeleteItemMessage>();
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"{msg.ResourceUrl} (source id: {id}): An unhandled exception occurred in the GetItemForDeletion block: {ex}");
                        throw;
                    }
                }, new ExecutionDataflowBlockOptions
                {
                    MaxDegreeOfParallelism = options.MaxDegreeOfParallelismForPostResourceItem
                });
            
            return getItemForDeletionBlock;

            string GetQueryStringValue(JProperty property)
            {
                var type = property.Value.Type;

                switch (type)
                {
                    case JTokenType.Date:
                        var dateValue = property.Value.Value<DateTime>();

                        if (dateValue == dateValue.Date)
                        {
                            return dateValue.ToString("yyyy-MM-dd");
                        }
                        
                        return JsonConvert.SerializeObject(property.Value).Trim('"');
                    case JTokenType.TimeSpan:
                        return JsonConvert.SerializeObject(property.Value).Trim('"');
                    default:
                        return property.Value.ToString();
                }
            }
        }
        
        private static TransformManyBlock<DeleteItemMessage, ErrorItemMessage> CreateDeleteResourceBlock(
            HttpClient targetHttpClient, Options options)
        {
            var deleteResource = new TransformManyBlock<DeleteItemMessage, ErrorItemMessage>(
                async msg =>
            {
                string id = msg.Id;
                string sourceId = msg.SourceId;

                try
                {
                    int attempts = 0;
                    int maxAttempts = 1 + Math.Max(0, options.MaxRetryAttempts);
                    int delay = options.RetryStartingDelayMilliseconds;

                    HttpResponseMessage apiResponse = null;
                    string responseContent = null;

                    while (++attempts <= maxAttempts)
                    {
                        try
                        {
                            if (attempts > 1)
                            {
                                if (_logger.IsDebugEnabled)
                                {
                                    _logger.Debug($"{msg.ResourceUrl} (source id: {sourceId}): DELETE attempt #{attempts}.");
                                }
                            }

                            apiResponse = await targetHttpClient.DeleteAsync($"{msg.ResourceUrl}/{id}").ConfigureAwait(false);

                            if (!apiResponse.IsSuccessStatusCode)
                            {
                                responseContent = await apiResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                                
                                _logger.Error(
                                    $"{msg.ResourceUrl} (source id: {sourceId}): DELETE returned {apiResponse.StatusCode}{Environment.NewLine}{responseContent}");

                                // Retry certain error types
                                if (apiResponse.StatusCode == HttpStatusCode.Conflict
                                    || !apiResponse.StatusCode.IsPermanentFailure())
                                {
                                    _logger.Warn(
                                        $"{msg.ResourceUrl} (source id: {sourceId}): Retrying delete resource (attempt #{attempts} failed with status '{apiResponse.StatusCode}').");

                                    ExponentialBackOffHelper.PerformDelay(ref delay);
                                    continue;
                                }
                            
                                // Publish the failure
                                var error = new ErrorItemMessage
                                {
                                    Method = HttpMethod.Delete.ToString(),
                                    ResourceUrl = msg.ResourceUrl,
                                    Id = id,
                                    Body = null,
                                    ResponseStatus = apiResponse.StatusCode,
                                    ResponseContent = responseContent
                                };

                                return new[] {error};
                            }
                        
                            // Success
                            if (_logger.IsInfoEnabled && attempts > 1)
                                _logger.Info(
                                    $"{msg.ResourceUrl} (source id: {sourceId}): DELETE attempt #{attempts} returned {apiResponse.StatusCode}.");

                            if (_logger.IsDebugEnabled)
                                _logger.Debug($"{msg.ResourceUrl} (source id: {sourceId}): DELETE returned {apiResponse.StatusCode}");

                            break;
                        }
                        catch (Exception ex)
                        {
                            _logger.Error($"{msg.ResourceUrl} (source id: {sourceId}): Delete attempt #{attempts} threw an exception: {ex}");

                            ExponentialBackOffHelper.PerformDelay(ref delay);
                        }
                    }

                    // If retry count exceeded with a failure response, publish the failure
                    if (attempts > maxAttempts && apiResponse?.IsSuccessStatusCode == false)
                    {
                        // Publish the failure
                        var error = new ErrorItemMessage
                        {
                            Method = HttpMethod.Delete.ToString(),
                            ResourceUrl = msg.ResourceUrl,
                            Id = id,
                            Body = null,
                            ResponseStatus = apiResponse?.StatusCode,
                            ResponseContent = responseContent
                        };

                        return new[] {error};
                    }

                    // Success - no errors to publish
                    return Enumerable.Empty<ErrorItemMessage>();
                }
                catch (Exception ex)
                {
                    _logger.Error($"{msg.ResourceUrl} (source id: {sourceId}): An unhandled exception occurred in the DeleteResource block: {ex}");
                    throw;
                }
            }, new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = options.MaxDegreeOfParallelismForPostResourceItem
            });

            return deleteResource;
        }

        public static GetItemForDeletionMessage CreateItemActionMessage(StreamResourcePageMessage<GetItemForDeletionMessage> msg, JObject j)
        {
            // Detect cancellation and quit returning messages
            if (msg.CancellationSource.IsCancellationRequested)
            {
                return null;
            }

            // If there are no key values on the message, cancel delete processing since the source
            // API isn't providing the information to publish deletes between ODS API instances
            if (j[EdFiApiConstants.KeyValuesPropertyName] == null)
            {
                // TODO: GKM - Should we add a flag for specifying that publishing without proper deletes support from source API is ok?
                _logger.Warn($"Source API's '{EdFiApiConstants.DeletesPathSuffix}' response does not include the domain key values. Publishing of deletes to the target API cannot be performed.");
                _logger.Debug("Attempting to gracefully cancel delete processing due to lack of support for deleted key values from the source API.");
                
                msg.CancellationSource.Cancel();
                
                return null;
            }

            return new GetItemForDeletionMessage
            {
                ResourceUrl = msg.ResourceUrl.TrimSuffix(EdFiApiConstants.DeletesPathSuffix),
                KeyValues = j[EdFiApiConstants.KeyValuesPropertyName],
                Id = j["id"].Value<string>(),
                CancellationToken = msg.CancellationSource.Token,
            };
        }
    }
}