using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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
    public static class ChangeResourceKey
    {
        private static readonly ILog _logger = LogManager.GetLogger(typeof(ChangeResourceKey));
        
        public static ValueTuple<ITargetBlock<GetItemForKeyChangeMessage>, ISourceBlock<ErrorItemMessage>> CreateBlocks(
            EdFiApiClient targetApiClient, Options options,
            ITargetBlock<ErrorItemMessage> errorHandlingBlock)
         {
            var getItemForKeyChangeBlock =
                CreateGetItemForKeyChangeBlock(targetApiClient.HttpClient, options, errorHandlingBlock);
            
            var changeKeyResourceBlock = CreateChangeKeyBlock(targetApiClient.HttpClient, options);
            
            getItemForKeyChangeBlock.LinkTo(changeKeyResourceBlock, new DataflowLinkOptions {PropagateCompletion = true});
            
            return
                ((ITargetBlock<GetItemForKeyChangeMessage>) getItemForKeyChangeBlock, 
                    (ISourceBlock<ErrorItemMessage>) changeKeyResourceBlock);
         }
        
        private static TransformManyBlock<GetItemForKeyChangeMessage, ChangeKeyMessage> CreateGetItemForKeyChangeBlock(
            HttpClient targetHttpClient, 
            Options options, 
            ITargetBlock<ErrorItemMessage> errorHandlingBlock)
        {
            var getItemForKeyChangeBlock = new TransformManyBlock<GetItemForKeyChangeMessage, ChangeKeyMessage>(
                async msg =>
                {
                    // If the message wasn't created (because there is no natural key information)
                    if (msg == null)
                    {
                        return Enumerable.Empty<ChangeKeyMessage>();
                    }
                    
                    string sourceId = msg.SourceId;

                    try
                    {
                        var keyValueParms = msg.ExistingKeyValues
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
                                        _logger.Debug($"{msg.ResourceUrl} (source id: {msg.SourceId}): GET by key on target attempt #{attempts} ({queryString}).");
                                    }
                                }

                                apiResponse = await targetHttpClient.GetAsync($"{msg.ResourceUrl}?{queryString}").ConfigureAwait(false);

                                responseContent = await apiResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

                                if (!apiResponse.IsSuccessStatusCode)
                                {
                                    _logger.Error(
                                        $"{msg.ResourceUrl} (source id: {sourceId}): GET by key returned {apiResponse.StatusCode}{Environment.NewLine}{responseContent}");

                                    // Retry certain error types
                                    if (!apiResponse.StatusCode.IsPermanentFailure())
                                    {
                                        _logger.Warn(
                                            $"{msg.ResourceUrl} (source id: {sourceId}): Retrying select by key on resource (attempt #{attempts} failed with status '{apiResponse.StatusCode}').");

                                        ExponentialBackOffHelper.PerformDelay(ref delay);
                                        continue;
                                    }
                            
                                    var error = new ErrorItemMessage
                                    {
                                        Method = HttpMethod.Get.ToString(),
                                        ResourceUrl = msg.ResourceUrl,
                                        Id = sourceId,
                                        Body = null,
                                        ResponseStatus = apiResponse.StatusCode,
                                        ResponseContent = responseContent
                                    };

                                    // Publish the failure
                                    errorHandlingBlock.Post(error);
                                
                                    // No delete to process
                                    return Enumerable.Empty<ChangeKeyMessage>();
                                }

                                // Success
                                if (_logger.IsInfoEnabled && attempts > 1)
                                {
                                    _logger.Info(
                                        $"{msg.ResourceUrl} (source id: {sourceId}): GET by key attempt #{attempts} returned {apiResponse.StatusCode}.");
                                }

                                if (_logger.IsDebugEnabled)
                                {
                                    _logger.Debug($"{msg.ResourceUrl} (source id: {sourceId}): GET by key returned {apiResponse.StatusCode}");
                                }

                                var getByKeyResults = JArray.Parse(responseContent);

                                // If the item whose key is to be changed cannot be found...
                                if (getByKeyResults.Count == 0)
                                {
                                    if (_logger.IsWarnEnabled)
                                    {
                                        _logger.Warn($"{msg.ResourceUrl} (source id: {sourceId}): GET by key for key change returned no results on target API ({queryString}).");
                                    }
                                    
                                    return Enumerable.Empty<ChangeKeyMessage>();
                                }
                                
                                // Get the resource item
                                var existingResourceItem = getByKeyResults[0] as JObject;

                                // Remove the id and etag properties
                                string targetId = existingResourceItem["id"].Value<string>(); 
                                existingResourceItem.Property("id")?.Remove();
                                existingResourceItem.Property("_etag")?.Remove();

                                var candidateProperties = existingResourceItem.Properties()
                                    .Where(p => p.Value.Type != JTokenType.Object && p.Value.Type != JTokenType.Array)
                                    .Concat(
                                        existingResourceItem.Properties()
                                            .Where(p => p.Value.Type == JTokenType.Object && p.Name.EndsWith("Reference"))
                                            .SelectMany(reference => {
                                                // Remove the link from the reference while we're here
                                                var referenceAsJObject = reference.Value as JObject;
                                                referenceAsJObject.Property("link")?.Remove();
                    
                                                // Return the reference's properties as potential keyChange value updates
                                                return referenceAsJObject.Properties();
                                            })
                                    );

                                var newKeyValues = msg.NewKeyValues as JObject;

                                foreach (var candidateProperty in candidateProperties)
                                {
                                    var newValueProperty = newKeyValues.Property(candidateProperty.Name);

                                    if (newValueProperty != null) 
                                    {
                                        if (_logger.IsDebugEnabled)
                                        {
                                            _logger.Debug($"{msg.ResourceUrl} (source id: {msg.SourceId}): Assigning new value for '{candidateProperty.Name}' as '{newValueProperty.Value}'...");
                                        }
                                        
                                        candidateProperty.Value = newValueProperty.Value;
                                    }
                                }

                                return new[]
                                {
                                    new ChangeKeyMessage
                                    {
                                        ResourceUrl = msg.ResourceUrl,
                                        Id = targetId,
                                        Body = existingResourceItem.ToString(),
                                        SourceId = msg.SourceId,
                                    }
                                };
                            }
                            catch (Exception ex)
                            {
                                _logger.Error($"{msg.ResourceUrl} (source id: {sourceId}): GET by key attempt #{attempts}): {ex}");

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
                                Id = sourceId,
                                Body = null,
                                ResponseStatus = apiResponse?.StatusCode,
                                ResponseContent = responseContent
                            };

                            // Publish the failure
                            errorHandlingBlock.Post(error);
                        }

                        // Success - no errors to publish
                        return Enumerable.Empty<ChangeKeyMessage>();
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"{msg.ResourceUrl} (source id: {sourceId}): An unhandled exception occurred in the GetItemForKeyChange block: {ex}");
                        throw;
                    }
                }, new ExecutionDataflowBlockOptions
                {
                    MaxDegreeOfParallelism = options.MaxDegreeOfParallelismForPostResourceItem
                });
            
            return getItemForKeyChangeBlock;

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
        
        private static TransformManyBlock<ChangeKeyMessage, ErrorItemMessage> CreateChangeKeyBlock(
            HttpClient targetHttpClient, Options options)
        {
            var changeKey = new TransformManyBlock<ChangeKeyMessage, ErrorItemMessage>(
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

                            apiResponse = await targetHttpClient.PutAsync(
                                    $"{msg.ResourceUrl}/{id}",
                                    new StringContent(msg.Body, Encoding.UTF8, "application/json"))
                                .ConfigureAwait(false);

                            if (!apiResponse.IsSuccessStatusCode)
                            {
                                responseContent = await apiResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                                
                                _logger.Error(
                                    $"{msg.ResourceUrl} (source id: {sourceId}): PUT returned {apiResponse.StatusCode}{Environment.NewLine}{responseContent}");

                                // Retry certain error types
                                if (apiResponse.StatusCode == HttpStatusCode.Conflict
                                    || !apiResponse.StatusCode.IsPermanentFailure())
                                {
                                    _logger.Warn(
                                        $"{msg.ResourceUrl} (source id: {sourceId}): Retrying key change on resource (attempt #{attempts} failed with status '{apiResponse.StatusCode}').");

                                    ExponentialBackOffHelper.PerformDelay(ref delay);
                                    continue;
                                }

                                // Publish the failure
                                var error = new ErrorItemMessage
                                {
                                    Method = HttpMethod.Put.ToString(),
                                    ResourceUrl = msg.ResourceUrl,
                                    Id = id,
                                    Body = ParseToJObjectOrDefault(msg.Body),
                                    ResponseStatus = apiResponse.StatusCode,
                                    ResponseContent = responseContent
                                };

                                return new[] {error};
                            }
                        
                            // Success
                            if (_logger.IsInfoEnabled && attempts > 1)
                                _logger.Info(
                                    $"{msg.ResourceUrl} (source id: {sourceId}): PUT attempt #{attempts} returned {apiResponse.StatusCode}.");

                            if (_logger.IsDebugEnabled)
                                _logger.Debug($"{msg.ResourceUrl} (source id: {sourceId}): PUT returned {apiResponse.StatusCode}");

                            break;
                        }
                        catch (Exception ex)
                        {
                            _logger.Error($"{msg.ResourceUrl} (source id: {sourceId}): Key change attempt #{attempts} threw an exception: {ex}");

                            ExponentialBackOffHelper.PerformDelay(ref delay);
                        }
                    }

                    // If retry count exceeded with a failure response, publish the failure
                    if (attempts > maxAttempts && apiResponse?.IsSuccessStatusCode == false)
                    {
                        // Publish the failure
                        var error = new ErrorItemMessage
                        {
                            Method = HttpMethod.Put.ToString(),
                            ResourceUrl = msg.ResourceUrl,
                            Id = id,
                            Body = ParseToJObjectOrDefault(msg.Body),
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
                    _logger.Error($"{msg.ResourceUrl} (source id: {sourceId}): An unhandled exception occurred in the ChangeResourceKey block: {ex}");
                    throw;
                }
            }, new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = options.MaxDegreeOfParallelismForPostResourceItem
            });

            return changeKey;

            JObject ParseToJObjectOrDefault(string json)
            {
                JObject body = null;

                try
                {
                    body = JObject.Parse(json);
                }
                catch
                {
                    // ignored
                }

                return body;
            }
        }

        public static GetItemForKeyChangeMessage CreateItemActionMessage(StreamResourcePageMessage<GetItemForKeyChangeMessage> msg, JObject j)
        {
            // Detect cancellation and quit returning messages
            if (msg.CancellationSource.IsCancellationRequested)
            {
                return null;
            }
            
            // If there are no key values on the message, cancel key change processing since the source
            // API isn't providing the information to publish key changes between ODS API instances
            if (j[EdFiApiConstants.OldKeyValuesPropertyName] == null)
            {
                // TODO: GKM - Should we add a flag for specifying that publishing without proper key change support from source API is ok?
                _logger.Warn($"Source API's '{EdFiApiConstants.KeyChangesPathSuffix}' response does not include the domain key values. Publishing of key changes to the target API cannot be performed.");
                _logger.Debug("Attempting to gracefully cancel key change processing due to lack of support for key values from the source API.");
                
                msg.CancellationSource.Cancel();
                
                return null;
            }
            
            return new GetItemForKeyChangeMessage
            {
                ResourceUrl = msg.ResourceUrl.TrimSuffix(EdFiApiConstants.KeyChangesPathSuffix),
                ExistingKeyValues = j[EdFiApiConstants.OldKeyValuesPropertyName],
                NewKeyValues = j[EdFiApiConstants.NewKeyValuesPropertyName],
                SourceId = j["id"].Value<string>(),
                CancellationToken = msg.CancellationSource.Token,
            };
        }
    }
}