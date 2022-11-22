using System.Net;
using System.Text;
using System.Threading.Tasks.Dataflow;
using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Messages;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Extensions;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Blocks;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using log4net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Polly;
using Polly.Contrib.WaitAndRetry;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Blocks
{
    /// <summary>
    /// Builds a pipeline that processes key changes against a target Ed-Fi ODS API.
    /// </summary>
    /// <remarks>Receives a <see cref="GetItemForKeyChangeMessage" />, transforms to a <see cref="ChangeKeyMessage" /> before
    /// producing <see cref="ErrorItemMessage" /> instances as output. </remarks>
    public class ChangeResourceKeyProcessingBlocksFactory : IProcessingBlocksFactory<GetItemForKeyChangeMessage>
    {
        private readonly ITargetEdFiApiClientProvider _targetEdFiApiClientProvider;
        
        private readonly ILog _logger = LogManager.GetLogger(typeof(ChangeResourceKeyProcessingBlocksFactory));

        public ChangeResourceKeyProcessingBlocksFactory(ITargetEdFiApiClientProvider targetEdFiApiClientProvider)
        {
            _targetEdFiApiClientProvider = targetEdFiApiClientProvider;
        }
        
        public (ITargetBlock<GetItemForKeyChangeMessage>, ISourceBlock<ErrorItemMessage>) CreateProcessingBlocks(
            CreateBlocksRequest createBlocksRequest)
        {
            TransformManyBlock<GetItemForKeyChangeMessage, ChangeKeyMessage> getItemForKeyChangeBlock
                = CreateGetItemForKeyChangeBlock(
                    _targetEdFiApiClientProvider.GetApiClient(),
                    createBlocksRequest.Options,
                    createBlocksRequest.ErrorHandlingBlock);

            TransformManyBlock<ChangeKeyMessage, ErrorItemMessage> changeKeyResourceBlock 
                = CreateChangeKeyBlock(_targetEdFiApiClientProvider.GetApiClient(), createBlocksRequest.Options);

            getItemForKeyChangeBlock.LinkTo(changeKeyResourceBlock, new DataflowLinkOptions { PropagateCompletion = true });

            return (getItemForKeyChangeBlock, changeKeyResourceBlock);
        }

        private TransformManyBlock<GetItemForKeyChangeMessage, ChangeKeyMessage> CreateGetItemForKeyChangeBlock(
            EdFiApiClient targetApiClient, 
            Options options, 
            ITargetBlock<ErrorItemMessage> errorHandlingBlock)
        {
            var getItemForKeyChangeBlock = new TransformManyBlock<GetItemForKeyChangeMessage, ChangeKeyMessage>(
                async message =>
                {
                    // If the message wasn't created (because there is no natural key information)
                    if (message == null)
                    {
                        return Enumerable.Empty<ChangeKeyMessage>();
                    }
                    
                    string sourceId = message.SourceId;

                    try
                    {
                        var keyValueParms = message.ExistingKeyValues
                            .OfType<JProperty>()
                            .Select(p => $"{p.Name}={WebUtility.UrlEncode(GetQueryStringValue(p))}");

                        string queryString = String.Join("&", keyValueParms);
                    
                        var delay = Backoff.ExponentialBackoff(
                            TimeSpan.FromMilliseconds(options.RetryStartingDelayMilliseconds),
                            options.MaxRetryAttempts);

                        int attempts = 0;
                        
                        var apiResponse = await Policy
                            .Handle<Exception>()
                            .OrResult<HttpResponseMessage>(r => r.StatusCode.IsPotentiallyTransientFailure())
                            .WaitAndRetryAsync(delay, (result, ts, retryAttempt, ctx) =>
                            {
                                if (result.Exception != null)
                                {
                                    _logger.Error($"{message.ResourceUrl} (source id: {sourceId}): GET by key on resource failed with an exception. Retrying... (retry #{retryAttempt} of {options.MaxRetryAttempts} with {ts.TotalSeconds:N1}s delay){Environment.NewLine}{result.Exception}");
                                }
                                else
                                {
                                    _logger.Warn(
                                        $"{message.ResourceUrl} (source id: {sourceId}): GET by key on resource failed with status '{result.Result.StatusCode}'. Retrying... (retry #{retryAttempt} of {options.MaxRetryAttempts} with {ts.TotalSeconds:N1}s delay)");
                                }
                            })
                            .ExecuteAsync((ctx, ct) =>
                            {
                                attempts++;

                                if (attempts > 1)
                                {
                                    if (_logger.IsDebugEnabled)
                                    {
                                        _logger.Debug($"{message.ResourceUrl} (source id: {message.SourceId}): GET by key on target attempt #{attempts} ({queryString}).");
                                    }
                                }

                                return targetApiClient.HttpClient.GetAsync($"{targetApiClient.DataManagementApiSegment}{message.ResourceUrl}?{queryString}", ct);
                            }, new Context(), CancellationToken.None);

                        // Detect null content and provide a better error message (which happens during unit testing if mocked requests aren't properly defined)
                        if (apiResponse.Content == null)
                        {
                            throw new NullReferenceException($"Content of response for '{targetApiClient.HttpClient.BaseAddress}{message.ResourceUrl}?{queryString}' was null.");
                        }
                        
                        string responseContent = await apiResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                        
                        // Failure
                        if (!apiResponse.IsSuccessStatusCode)
                        {
                            _logger.Error(
                                $"{message.ResourceUrl} (source id: {sourceId}): GET by key returned {apiResponse.StatusCode}{Environment.NewLine}{responseContent}");

                            var error = new ErrorItemMessage
                            {
                                Method = HttpMethod.Get.ToString(),
                                ResourceUrl = $"{message.ResourceUrl}?{queryString}",
                                Id = sourceId,
                                Body = null,
                                ResponseStatus = apiResponse?.StatusCode,
                                ResponseContent = responseContent
                            };

                            // Publish the failure
                            errorHandlingBlock.Post(error);
                        
                            // No key changes to process
                            return Enumerable.Empty<ChangeKeyMessage>();
                        }

                        // Success
                        if (_logger.IsInfoEnabled && attempts > 1)
                        {
                            _logger.Info(
                                $"{message.ResourceUrl} (source id: {sourceId}): GET by key attempt #{attempts} returned {apiResponse.StatusCode}.");
                        }

                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug($"{message.ResourceUrl} (source id: {sourceId}): GET by key returned {apiResponse.StatusCode}");
                        }
                        
                        var getByKeyResults = JArray.Parse(responseContent);

                        // If the item whose key is to be changed cannot be found...
                        if (getByKeyResults.Count == 0)
                        {
                            if (_logger.IsWarnEnabled)
                            {
                                _logger.Warn($"{message.ResourceUrl} (source id: {sourceId}): GET by key for key change returned no results on target API ({queryString}).");
                            }
                            
                            // No key changes to process
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

                        var newKeyValues = message.NewKeyValues as JObject;

                        foreach (var candidateProperty in candidateProperties)
                        {
                            var newValueProperty = newKeyValues.Property(candidateProperty.Name);

                            if (newValueProperty != null) 
                            {
                                if (_logger.IsDebugEnabled)
                                {
                                    _logger.Debug($"{message.ResourceUrl} (source id: {message.SourceId}): Assigning new value for '{candidateProperty.Name}' as '{newValueProperty.Value}'...");
                                }
                                
                                candidateProperty.Value = newValueProperty.Value;
                            }
                        }

                        return new[]
                        {
                            new ChangeKeyMessage
                            {
                                ResourceUrl = message.ResourceUrl,
                                Id = targetId,
                                Body = existingResourceItem.ToString(),
                                SourceId = message.SourceId,
                            }
                        };
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"{message.ResourceUrl} (source id: {sourceId}): An unhandled exception occurred in the block created by '{nameof(CreateGetItemForKeyChangeBlock)}': {ex}");
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
        
        private TransformManyBlock<ChangeKeyMessage, ErrorItemMessage> CreateChangeKeyBlock(
            EdFiApiClient targetApiClient, Options options)
        {
            var changeKey = new TransformManyBlock<ChangeKeyMessage, ErrorItemMessage>(
                async msg =>
            {
                string id = msg.Id;
                string sourceId = msg.SourceId;

                try
                {
                    var delay = Backoff.ExponentialBackoff(
                        TimeSpan.FromMilliseconds(options.RetryStartingDelayMilliseconds),
                        options.MaxRetryAttempts);

                    int attempt = 0;

                    var apiResponse = await Policy
                        .Handle<Exception>()
                        .OrResult<HttpResponseMessage>(r => 
                            r.StatusCode == HttpStatusCode.Conflict || r.StatusCode.IsPotentiallyTransientFailure())
                        .WaitAndRetryAsync(delay, (result, ts, retryAttempt, ctx) =>
                        {
                            if (result.Exception != null)
                            {
                                _logger.Error($"{msg.ResourceUrl} (source id: {sourceId}): Key change attempt #{attempt} threw an exception: {result.Exception}");
                            }
                            else
                            {
                                _logger.Warn(
                                    $"{msg.ResourceUrl} (source id: {id}): Select by key on target resource failed with status '{result.Result.StatusCode}'. Retrying... (retry #{retryAttempt} of {options.MaxRetryAttempts} with {ts.TotalSeconds:N1}s delay)");
                            }
                        })
                        .ExecuteAsync((ctx, ct) =>
                        {
                            attempt++;

                            if (attempt > 1)
                            {
                                if (_logger.IsDebugEnabled)
                                {
                                    _logger.Debug($"{msg.ResourceUrl} (source id: {sourceId}): PUT request to update key (attempt #{attempt}.");
                                }
                            }
                                
                            return targetApiClient.HttpClient.PutAsync(
                                $"{targetApiClient.DataManagementApiSegment}{msg.ResourceUrl}/{id}",
                                new StringContent(msg.Body, Encoding.UTF8, "application/json"), 
                                ct);
                        }, new Context(), CancellationToken.None);
                    
                    // Failure
                    if (!apiResponse.IsSuccessStatusCode)
                    {
                        string responseContent = await apiResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                                
                        _logger.Error(
                            $"{msg.ResourceUrl} (source id: {sourceId}): PUT returned {apiResponse.StatusCode}{Environment.NewLine}{responseContent}");

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
                    if (_logger.IsInfoEnabled && attempt > 1)
                    {
                        _logger.Info(
                            $"{msg.ResourceUrl} (source id: {sourceId}): PUT attempt #{attempt} returned {apiResponse.StatusCode}.");
                    }

                    if (_logger.IsDebugEnabled)
                    {
                        _logger.Debug($"{msg.ResourceUrl} (source id: {sourceId}): PUT returned {apiResponse.StatusCode}");
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

            JObject? ParseToJObjectOrDefault(string json)
            {
                JObject? body = null;

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

        public IEnumerable<GetItemForKeyChangeMessage> CreateProcessDataMessages(StreamResourcePageMessage<GetItemForKeyChangeMessage> message, string json)
        {
            // Detect cancellation and quit returning messages
            if (message.CancellationSource.IsCancellationRequested)
            {
                yield break;
            }

            JArray items = JArray.Parse(json);

            // Iterate through the page of items
            foreach (var item in items.OfType<JObject>())
            {
                // Detect cancellation and quit returning messages
                if (message.CancellationSource.IsCancellationRequested)
                {
                    yield break;
                }

                var itemMessage = CreateMessage(item);

                if (itemMessage == null)
                {
                    yield break;
                }

                yield return itemMessage;
            }
            
            GetItemForKeyChangeMessage? CreateMessage(JObject obj)
            {
                // If there are no key values on the message, cancel key change processing since the source
                // API isn't providing the information to publish key changes between ODS API instances
                if (obj[EdFiApiConstants.OldKeyValuesPropertyName] == null)
                {
                    // TODO: GKM - Should we add a flag for specifying that publishing without proper key change support from source API is ok?
                    _logger.Warn($"Source API's '{EdFiApiConstants.KeyChangesPathSuffix}' response does not include the domain key values. Publishing of key changes to the target API cannot be performed.");
                    _logger.Debug("Attempting to gracefully cancel key change processing due to lack of support for key values from the source API.");
                
                    message.CancellationSource.Cancel();
                
                    return null;
                }
            
                return new GetItemForKeyChangeMessage
                {
                    ResourceUrl = message.ResourceUrl.TrimSuffix(EdFiApiConstants.KeyChangesPathSuffix),
                    ExistingKeyValues = obj[EdFiApiConstants.OldKeyValuesPropertyName],
                    NewKeyValues = obj[EdFiApiConstants.NewKeyValuesPropertyName],
                    SourceId = obj["id"].Value<string>(),
                    CancellationToken = message.CancellationSource.Token,
                };
            }
        }
    }
}