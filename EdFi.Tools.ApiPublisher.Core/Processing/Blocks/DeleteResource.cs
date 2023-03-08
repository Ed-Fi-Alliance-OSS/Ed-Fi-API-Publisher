using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks.Dataflow;
using EdFi.Tools.ApiPublisher.Core.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Extensions;
using EdFi.Tools.ApiPublisher.Core.Helpers;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using Serilog;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Polly;
using Polly.Contrib.WaitAndRetry;
using Serilog.Events;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Blocks
{
    public static class DeleteResource
    {
        private static readonly ILogger _logger = Log.Logger.ForContext(typeof(DeleteResource));
        
        public static ValueTuple<ITargetBlock<GetItemForDeletionMessage>, ISourceBlock<ErrorItemMessage>> CreateBlocks(
            CreateBlocksRequest createBlocksRequest)
        {
            var getItemForDeletionBlock = CreateGetItemForDeletionBlock(
                createBlocksRequest.TargetApiClient,
                createBlocksRequest.Options,
                createBlocksRequest.ErrorHandlingBlock);

            var deleteResourceBlock = CreateDeleteResourceBlock(createBlocksRequest.TargetApiClient, createBlocksRequest.Options);

            getItemForDeletionBlock.LinkTo(deleteResourceBlock, new DataflowLinkOptions {PropagateCompletion = true});
            
            return (getItemForDeletionBlock, deleteResourceBlock);
        }

        private static TransformManyBlock<GetItemForDeletionMessage, DeleteItemMessage> CreateGetItemForDeletionBlock(
            EdFiApiClient targetApiClient, 
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
                                    _logger.Error($"{msg.ResourceUrl} (source id: {id}): GET by key for deletion of target resource attempt #{attempts}): {result.Exception}");
                                }
                                else
                                {
                                    _logger.Warning(
                                        $"{msg.ResourceUrl} (source id: {id}): GET by key for deletion of target resource failed with status '{result.Result.StatusCode}'. Retrying... (retry #{retryAttempt} of {options.MaxRetryAttempts} with {ts.TotalSeconds:N1}s delay)");
                                }
                            })
                            .ExecuteAsync((ctx, ct) =>
                            {
                                attempts++;

                                if (attempts > 1)
                                {
                                    if (_logger.IsEnabled(LogEventLevel.Debug))
                                    {
                                        _logger.Debug($"{msg.ResourceUrl} (source id: {msg.Id}): GET by key for deletion of target resource (attempt #{attempts}) using '{queryString}'...");
                                    }
                                }
                                
                                return targetApiClient.HttpClient.GetAsync($"{targetApiClient.DataManagementApiSegment}{msg.ResourceUrl}?{queryString}", ct);
                            }, new Context(), CancellationToken.None);

                        string responseContent = null;

                        responseContent = await apiResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

                        if (!apiResponse.IsSuccessStatusCode)
                        {
                            _logger.Error(
                                $"{msg.ResourceUrl} (source id: {id}): GET by key returned {apiResponse.StatusCode}{Environment.NewLine}{responseContent}");

                            var error = new ErrorItemMessage
                            {
                                Method = HttpMethod.Get.ToString(),
                                ResourceUrl = $"{msg.ResourceUrl}?{queryString}",
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
                        
                        // Log a message if this was successful after a retry.
                        if (_logger.IsEnabled(LogEventLevel.Information) && attempts > 1)
                        {
                            _logger.Information(
                                $"{msg.ResourceUrl} (source id: {id}): GET by key attempt #{attempts} returned {apiResponse.StatusCode}.");
                        }

                        if (_logger.IsEnabled(LogEventLevel.Debug))
                        {
                            _logger.Debug($"{msg.ResourceUrl} (source id: {id}): GET by key returned {apiResponse.StatusCode}");
                        }

                        var getByKeyResults = JArray.Parse(responseContent);

                        // If the item to be deleted cannot be found...
                        if (getByKeyResults.Count == 0)
                        {
                            if (_logger.IsEnabled(LogEventLevel.Debug))
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
            EdFiApiClient targetApiClient, Options options)
        {
            var deleteResource = new TransformManyBlock<DeleteItemMessage, ErrorItemMessage>(
                async msg =>
            {
                string id = msg.Id;
                string sourceId = msg.SourceId;

                try
                {
                    var delay = Backoff.ExponentialBackoff(
                        TimeSpan.FromMilliseconds(options.RetryStartingDelayMilliseconds),
                        options.MaxRetryAttempts);

                    int attempts = 0;

                    var apiResponse = await Policy
                        .Handle<Exception>()
                        .OrResult<HttpResponseMessage>(r => 
                            r.StatusCode == HttpStatusCode.Conflict || r.StatusCode.IsPotentiallyTransientFailure())
                        .WaitAndRetryAsync(delay, (result, ts, retryAttempt, ctx) =>
                        {
                            if (result.Exception != null)
                            {
                                _logger.Error($"{msg.ResourceUrl} (source id: {sourceId}): Delete resource attempt #{attempts} threw an exception: {result.Exception}");
                            }
                            else
                            {
                                _logger.Warning(
                                    $"{msg.ResourceUrl} (source id: {sourceId}): Delete resource failed with status '{result.Result.StatusCode}'. Retrying... (retry #{retryAttempt} of {options.MaxRetryAttempts} with {ts.TotalSeconds:N1}s delay)");
                            }
                        })
                        .ExecuteAsync((ctx, ct) =>
                        {
                            attempts++;

                            if (attempts > 1)
                            {
                                if (_logger.IsEnabled(LogEventLevel.Debug))
                                {
                                    _logger.Debug($"{msg.ResourceUrl} (source id: {sourceId}): DELETE request (attempt #{attempts}.");
                                }
                            }
                                
                            return targetApiClient.HttpClient.DeleteAsync($"{targetApiClient.DataManagementApiSegment}{msg.ResourceUrl}/{id}", ct);
                        }, new Context(), CancellationToken.None);
                    
                    // Failure
                    if (!apiResponse.IsSuccessStatusCode)
                    {
                        string responseContent = await apiResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                                
                        _logger.Error(
                            $"{msg.ResourceUrl} (source id: {sourceId}): DELETE returned {apiResponse.StatusCode}{Environment.NewLine}{responseContent}");

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
                    if (_logger.IsEnabled(LogEventLevel.Information) && attempts > 1)
                    {
                        _logger.Information(
                            $"{msg.ResourceUrl} (source id: {sourceId}): DELETE attempt #{attempts} returned {apiResponse.StatusCode}.");
                    }

                    if (_logger.IsEnabled(LogEventLevel.Debug))
                    {
                        _logger.Debug($"{msg.ResourceUrl} (source id: {sourceId}): DELETE returned {apiResponse.StatusCode}");
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
                _logger.Warning($"Source API's '{EdFiApiConstants.DeletesPathSuffix}' response does not include the domain key values. Publishing of deletes to the target API cannot be performed.");
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