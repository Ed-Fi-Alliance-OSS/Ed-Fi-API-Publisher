using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks.Dataflow;
using EdFi.Common.Inflection;
using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Connections.Api.Configuration;
using EdFi.Tools.ApiPublisher.Connections.Api.DependencyResolution;
using EdFi.Tools.ApiPublisher.Connections.Api.Helpers;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Messages;
using EdFi.Tools.ApiPublisher.Core.Capabilities;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Extensions;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Blocks;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using Jering.Javascript.NodeJS;
using log4net;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json.Linq;
using Polly;
using Polly.Contrib.WaitAndRetry;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Blocks
{
    public class PostResourceProcessingBlocksFactory : IProcessingBlocksFactory<PostItemMessage>
    {
        private readonly ILog _logger = LogManager.GetLogger(typeof(PostResourceProcessingBlocksFactory));
        private readonly INodeJSService _nodeJsService;
        private readonly ITargetEdFiApiClientProvider _targetEdFiApiClientProvider;
        private readonly ISourceConnectionDetails _sourceConnectionDetails;
        private readonly ISourceCapabilities _sourceCapabilities;
        private readonly ISourceResourceItemProvider _sourceResourceItemProvider;

        public PostResourceProcessingBlocksFactory(
            INodeJSService nodeJsService,
            ITargetEdFiApiClientProvider targetEdFiApiClientProvider,
            ISourceConnectionDetails sourceConnectionDetails,
            ISourceCapabilities sourceCapabilities,
            ISourceResourceItemProvider sourceResourceItemProvider)
        {
            _nodeJsService = nodeJsService;
            _targetEdFiApiClientProvider = targetEdFiApiClientProvider;
            _sourceConnectionDetails = sourceConnectionDetails;
            _sourceCapabilities = sourceCapabilities;
            _sourceResourceItemProvider = sourceResourceItemProvider;
            
            // Ensure that the API connections are configured correctly with regards to Profiles
            // If we have no Profile applied to the target... ensure that the source also has no profile specified (to prevent accidental data loss on POST)
            if (string.IsNullOrEmpty(targetEdFiApiClientProvider.GetApiClient().ConnectionDetails.ProfileName))
            {
                // If the source is an API
                if (sourceConnectionDetails is ApiConnectionDetails sourceApiConnectionDetails)
                {
                    // If the source API is not using a Profile, prevent processing by throwing an exception
                    if (!string.IsNullOrEmpty(sourceApiConnectionDetails.ProfileName))
                    {
                        throw new Exception(
                            "The source API connection has a ProfileName specified, but the target API connection does not. POST requests against a target API without the Profile-based context of the source data can lead to accidental data loss.");
                    }
                }
            }
        }

        public (ITargetBlock<PostItemMessage>, ISourceBlock<ErrorItemMessage>) CreateProcessingBlocks(
            CreateBlocksRequest createBlocksRequest)
        {
            var knownUnremediatedRequests = new HashSet<(string resourceUrl, HttpStatusCode statusCode)>();

            var options = createBlocksRequest.Options;
            
            var targetEdFiApiClient = _targetEdFiApiClientProvider.GetApiClient();

            var javaScriptModuleFactory = createBlocksRequest.JavaScriptModuleFactory;

            var ignoredResourceByUrl = new ConcurrentDictionary<string, bool>();

            var missingDependencyByResourcePath = new Dictionary<string, string>();

            var items = createBlocksRequest.AuthorizationFailureHandling.SelectMany(
                h => h.UpdatePrerequisitePaths.Select(
                    prerequisite => new
                    {
                        ResourcePath = prerequisite,
                        DependencyResourcePath = h.Path
                    }));

            foreach (var item in items)
            {
                missingDependencyByResourcePath.Add(item.ResourcePath, item.DependencyResourcePath);
            }

            var postResourceBlock = new TransformManyBlock<PostItemMessage, ErrorItemMessage>(
                async msg => 
                    await HandlePostItemMessage(
                        ignoredResourceByUrl, 
                        msg, 
                        options, 
                        javaScriptModuleFactory, 
                        targetEdFiApiClient,
                        knownUnremediatedRequests,
                        missingDependencyByResourcePath),
                new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelismForPostResourceItem });

            return (postResourceBlock, postResourceBlock);
        }

        // NOTE: Consider breaking this out into a separate message handler, similar to EdFiApiStreamResourcePageMessageHandler
        private async Task<IEnumerable<ErrorItemMessage>> HandlePostItemMessage(
            ConcurrentDictionary<string, bool> ignoredResourceByUrl,
            PostItemMessage postItemMessage,
            Options options,
            Func<string>? javaScriptModuleFactory,
            EdFiApiClient targetEdFiApiClient,
            HashSet<(string resourceUrl, HttpStatusCode statusCode)> knownUnremediatedRequests,
            Dictionary<string, string> missingDependencyByResourcePath)
        {
            if (ignoredResourceByUrl.ContainsKey(postItemMessage.ResourceUrl))
            {
                return Enumerable.Empty<ErrorItemMessage>();
            }

            string id = postItemMessage.Item.TryGetValue("id", out var idToken)
                ? idToken.Value<string>()
                : "(unknown)";

            try
            {
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug(
                        $"{postItemMessage.ResourceUrl} (source id: {id}): Processing PostItemMessage (with up to {options.MaxRetryAttempts} retries).");
                }

                // Remove attributes not usable between API instances
                postItemMessage.Item.Remove("id");
                postItemMessage.Item.Remove("_etag");
                postItemMessage.Item.Remove("_lastModifiedDate");

                // For descriptors, also strip the surrogate id
                if (postItemMessage.ResourceUrl.EndsWith("Descriptors"))
                {
                    string descriptorBaseName = postItemMessage.ResourceUrl.Split('/').Last().TrimEnd('s');
                    string descriptorIdPropertyName = $"{descriptorBaseName}Id";

                    postItemMessage.Item.Remove(descriptorIdPropertyName);
                }

                var delay = Backoff.ExponentialBackoff(
                    TimeSpan.FromMilliseconds(options.RetryStartingDelayMilliseconds),
                    options.MaxRetryAttempts);

                int attempts = 0;

                var apiResponse = await Policy.Handle<Exception>()
                    .OrResult<HttpResponseMessage>(
                        r =>

                            // Descriptor Conflicts are not to be retried
                            (r.StatusCode == HttpStatusCode.Conflict
                                && !postItemMessage.ResourceUrl.EndsWith("Descriptors", StringComparison.OrdinalIgnoreCase))
                            || r.StatusCode.IsPotentiallyTransientFailure()
                            || IsBadRequestForUnresolvedReferenceOfPrimaryRelationship(r, postItemMessage)
                            || (!r.IsSuccessStatusCode
                                && javaScriptModuleFactory != null
                                && MayHaveRemediation(postItemMessage.ResourceUrl, r.StatusCode)))
                    .WaitAndRetryAsync(
                        delay,
                        async (result, ts, retryAttempt, ctx) =>
                        {
                            if (javaScriptModuleFactory != null)
                            {
                                var remediationResult = await TryRemediateFailureAsync(
                                    javaScriptModuleFactory,
                                    retryAttempt,
                                    targetEdFiApiClient,
                                    _sourceConnectionDetails.Name,
                                    postItemMessage.ResourceUrl,
                                    id,
                                    result.Result,
                                    postItemMessage.Item.ToString());

                                if (!remediationResult.FoundRemediation)
                                {
                                    knownUnremediatedRequests.Add((postItemMessage.ResourceUrl, result.Result.StatusCode));

                                    return;
                                }

                                // Check for a modified request body, and save it to the context
                                if (remediationResult.ModifiedRequestBody is JsonElement modifiedRequestBody
                                    && modifiedRequestBody.ValueKind != JsonValueKind.Null)
                                {
                                    if (_logger.IsDebugEnabled)
                                    {
                                        string modifiedRequestBodyJson = JsonSerializer.Serialize(
                                            remediationResult.ModifiedRequestBody,
                                            new JsonSerializerOptions { WriteIndented = true });

                                        _logger.Debug(
                                            $"{postItemMessage.ResourceUrl} (source id: {id}): Remediation plan provided a modified request body: {modifiedRequestBodyJson} ");
                                    }

                                    ctx["ModifiedRequestBody"] = remediationResult.ModifiedRequestBody;
                                }
                            }

                            if (result.Exception != null)
                            {
                                _logger.Warn(
                                    $"{postItemMessage.ResourceUrl} (source id: {id}): POST attempt #{attempts} failed with an exception. Retrying... (retry #{retryAttempt} of {options.MaxRetryAttempts} with {ts.TotalSeconds:N1}s delay):{Environment.NewLine}{result.Exception}");
                            }
                            else
                            {
                                string responseContent = await result.Result.Content.ReadAsStringAsync().ConfigureAwait(false);

                                _logger.Warn(
                                    $"{postItemMessage.ResourceUrl} (source id: {id}): POST attempt #{attempts} failed with status '{result.Result.StatusCode}'. Retrying... (retry #{retryAttempt} of {options.MaxRetryAttempts} with {ts.TotalSeconds:N1}s delay):{Environment.NewLine}{responseContent}");
                            }
                        })
                    .ExecuteAsync(
                        async (ctx, ct) =>
                        {
                            attempts++;

                            if (_logger.IsDebugEnabled)
                            {
                                if (attempts > 1)
                                {
                                    _logger.Debug($"{postItemMessage.ResourceUrl} (source id: {id}): POST attempt #{attempts}.");
                                }
                                else
                                {
                                    _logger.Debug($"{postItemMessage.ResourceUrl} (source id: {id}): Sending POST request.");
                                }
                            }

                            // Prepare request body
                            string requestBodyJson;

                            if (ctx.TryGetValue("ModifiedRequestBody", out dynamic modifiedRequestBody))
                            {
                                _logger.Info(
                                    $"{postItemMessage.ResourceUrl} (source id: {id}): Applying modified request body from remediation plan...");

                                requestBodyJson = JsonSerializer.Serialize(modifiedRequestBody);
                            }
                            else
                            {
                                requestBodyJson = postItemMessage.Item.ToString();
                            }

                            var response = await RequestHelpers.SendPostRequestAsync(
                                targetEdFiApiClient,
                                postItemMessage.ResourceUrl,
                                $"{targetEdFiApiClient.DataManagementApiSegment}{postItemMessage.ResourceUrl}",
                                requestBodyJson,
                                ct);

                            var (hasMissingDependency, missingDependencyDetails) =
                                await TryGetMissingDependencyDetailsAsync(response, postItemMessage);

                            if (hasMissingDependency)
                            {
                                if (!_sourceCapabilities.SupportsGetItemById)
                                {
                                    _logger.Warn(
                                        $"{postItemMessage.ResourceUrl}: Reference '{missingDependencyDetails!.ReferenceName}' to resource '{missingDependencyDetails.ReferencedResourceName}' could not be automatically resolved because the source connection does not support retrieving items by id.");

                                    return response;
                                }

                                _logger.Info(
                                    $"{postItemMessage.ResourceUrl}: Attempting to retrieve missing '{missingDependencyDetails.ReferencedResourceName}' reference based on 'authorizationFailureHandling' metadata in apiPublisherSettings.json.");

                                var (missingDependencyItemRetrieved, missingItemJson) =
                                    await _sourceResourceItemProvider.TryGetResourceItemAsync(
                                        missingDependencyDetails.SourceDependencyItemUrl);

                                if (missingDependencyItemRetrieved)
                                {
                                    var missingItem = JObject.Parse(missingItemJson!);

                                    var postDependencyItemMessage = new PostItemMessage
                                    {
                                        ResourceUrl = missingDependencyDetails.DependencyResourceUrl,
                                        Item = missingItem,
                                        PostAuthorizationFailureRetry =
                                            postItemMessage.PostAuthorizationFailureRetry, // TODO: Is this appropriate to copy?
                                    };

                                    await HandlePostItemMessage(
                                        ignoredResourceByUrl,
                                        postDependencyItemMessage!,
                                        options,
                                        javaScriptModuleFactory,
                                        targetEdFiApiClient,
                                        knownUnremediatedRequests,
                                        missingDependencyByResourcePath);
                                }
                            }

                            return response;
                        },
                        new Context(),
                        CancellationToken.None);

                // Failure
                if (!apiResponse.IsSuccessStatusCode)
                {
                    // Descriptor POSTs behave slightly different than other resources in
                    // that the DescriptorId must be used to update the descriptor value,
                    // while a POST with the values will result in a 409 Conflict if the values
                    // already exist. Thus, a Conflict response can be safely ignored as it
                    // indicates the data is already present and nothing more needs to be done.
                    if (postItemMessage.ResourceUrl.EndsWith("Descriptors") && apiResponse.StatusCode == HttpStatusCode.Conflict)
                    {
                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug(
                                $"{postItemMessage.ResourceUrl} (source id: {id}): POST returned {HttpStatusCode.Conflict}, but for descriptors this means the value is already present.");
                        }

                        return Enumerable.Empty<ErrorItemMessage>();
                    }

                    // Gracefully handle authorization errors by using the retry action delegate
                    // (if present) to post the message to the retry "resource" queue 
                    if (apiResponse.StatusCode == HttpStatusCode.Forbidden)
                    {
                        // Determine if current resource has an authorization retry queue
                        if (postItemMessage.PostAuthorizationFailureRetry != null)
                        {
                            if (_logger.IsDebugEnabled)
                            {
                                _logger.Debug(
                                    $"{postItemMessage.ResourceUrl} (source id: {id}): Authorization failed -- deferring for retry after pertinent associations are processed.");
                            }

                            // Re-add the identifier, and pass the message along to the "retry" resource (after associations have been processed)
                            postItemMessage.Item.Add("id", idToken);
                            postItemMessage.PostAuthorizationFailureRetry(postItemMessage);

                            // Deferring for retry - no errors to publish
                            return Enumerable.Empty<ErrorItemMessage>();
                        }
                    }

                    string responseContent = await apiResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

                    // If the failure is Forbidden, and we should treat it as a warning
                    if (apiResponse.StatusCode == HttpStatusCode.Forbidden
                        && postItemMessage.PostAuthorizationFailureRetry == null
                        && targetEdFiApiClient.ConnectionDetails?.TreatForbiddenPostAsWarning == true)
                    {
                        // Warn and ignore all future data for this resource
                        _logger.Warn(
                            $"{postItemMessage.ResourceUrl} (source id: {id}): Authorization failed on POST of resource with no authorization failure handling defined. Remaining resource items will be ignored. Response status: {apiResponse.StatusCode}{Environment.NewLine}{responseContent}");

                        ignoredResourceByUrl.TryAdd(postItemMessage.ResourceUrl, true);

                        return Enumerable.Empty<ErrorItemMessage>();
                    }

                    // Error is final, log it and indicate failure for processing
                    _logger.Error(
                        $"{postItemMessage.ResourceUrl} (source id: {id}): POST attempt #{attempts} failed with status '{apiResponse.StatusCode}':{Environment.NewLine}{responseContent}");

                    // Publish the failed data
                    var error = new ErrorItemMessage
                    {
                        Method = HttpMethod.Post.ToString(),
                        ResourceUrl = postItemMessage.ResourceUrl,
                        Id = id,
                        Body = postItemMessage.Item,
                        ResponseStatus = apiResponse.StatusCode,
                        ResponseContent = responseContent
                    };

                    return new[] { error };
                }

                // Success
                if (attempts > 1)
                {
                    if (_logger.IsInfoEnabled)
                    {
                        _logger.Info(
                            $"{postItemMessage.ResourceUrl} (source id: {id}): POST attempt #{attempts} returned {apiResponse.StatusCode}.");
                    }
                }
                else
                {
                    // Ensure a log entry when POST succeeds on first attempt and DEBUG logging is enabled
                    if (_logger.IsDebugEnabled)
                    {
                        _logger.Debug(
                            $"{postItemMessage.ResourceUrl} (source id: {id}): POST attempt #{attempts} returned {apiResponse.StatusCode}.");
                    }
                }

                // Success - no errors to publish
                return Enumerable.Empty<ErrorItemMessage>();
            }
            catch (Exception ex)
            {
                _logger.Error(
                    $"{postItemMessage.ResourceUrl} (source id: {id}): An unhandled exception occurred in the PostResource block: {ex}");

                throw;
            }
            finally
            {
                // Drop reference to JObject so it can be GC'd.
                postItemMessage.Item = null;
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

            bool MayHaveRemediation(string resourceUrl, HttpStatusCode statusCode)
            {
                return !knownUnremediatedRequests.Contains((resourceUrl, statusCode));
            }

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

            async Task<(bool success, MissingDependencyDetails?)> TryGetMissingDependencyDetailsAsync(HttpResponseMessage postItemResponse, PostItemMessage msg)
            {
                // If response is a Bad Request (which is the API's error response for missing Staff/Student/Parent), check for need to explicitly fetch dependencies
                // NOTE: If support is expanded for other missing dependencies, the response code from the API (currently) will be a 409 Conflict status.
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
                                string referencedResourceName = referenceNameMatch.Groups["ReferencedResourceName"].Value;
                                string referenceName = referencedResourceName.ToCamelCase() + "Reference";

                                // Get the missing reference's source URL
                                string? dependencyItemUrl = msg.Item.SelectToken($"{referenceName}.link.href")?.Value<string>();

                                if (dependencyItemUrl == null)
                                {
                                    _logger.Warn($"{msg.ResourceUrl}: Unable to extract href to '{referenceName}' from JSON body for obtaining missing dependency.");
                                    return (false, null);
                                }

                                // URL is expected to be of the format of 
                                var parts = dependencyItemUrl.Split('/');

                                if (parts.Length < 3)
                                {
                                    _logger.Warn($"{msg.ResourceUrl}: Unable to identify missing dependency resource URL from the supplied dependency item URL '{dependencyItemUrl}'.");
                                }

                                try
                                {
                                    return (true, new MissingDependencyDetails()
                                    {
                                        DependentResourceUrl = msg.ResourceUrl,
                                        DependencyResourceUrl = $"/{parts[^3]}/{parts[^2]}",
                                        ReferenceName = referenceName,
                                        ReferencedResourceName = referencedResourceName,
                                        SourceDependencyItemUrl = dependencyItemUrl,
                                    });
                                }
                                catch (Exception ex)
                                {
                                    _logger.Warn($"{msg.ResourceUrl}: Unable to identify missing dependency resource URL from the supplied dependency item URL '{dependencyItemUrl}'.");
                                }
                            }
                        }
                    }
                }

                return (false, null);
            }
        }

        /// <summary>
        /// Contains details about a missing dependency.
        /// </summary>
        private record MissingDependencyDetails
        {
            /// <summary>
            /// The relative resource URL of the resource being processed for which a missing dependency was identified.
            /// </summary>
            public string DependentResourceUrl { get; set; }
            
            /// <summary>
            /// The property name of the reference which is the missing dependency.
            /// </summary>
            public string ReferenceName { get; set; }

            /// <summary>
            /// The name of the referenced resource.
            /// </summary>
            public string ReferencedResourceName { get; set; }

            /// <summary>
            /// The full URL to the source item that is missing. 
            /// </summary>
            public string SourceDependencyItemUrl { get; set; }

            /// <summary>
            /// The relative resource URL of the resource that represents the dependency.
            /// </summary>
            public string DependencyResourceUrl { get; set; }
        }
        
        public IEnumerable<PostItemMessage> CreateProcessDataMessages(StreamResourcePageMessage<PostItemMessage> message, string json)
        {
            JArray items = JArray.Parse(json);

            // Iterate through the page of items
            foreach (var item in items.OfType<JObject>())
            {
                var itemMessage = CreateItemActionMessage(message, item);

                // Stop processing individual items if cancellation has been requested
                if (message.CancellationSource.IsCancellationRequested)
                {
                    _logger.Debug(
                        $"{message.ResourceUrl}: Cancellation requested during item '{nameof(PostItemMessage)}' creation.");

                    yield break;
                }

                // Add the item to the buffer for processing into the target API
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug(
                        $"{message.ResourceUrl}: Adding individual action message of type '{nameof(PostItemMessage)}' for item '{item["id"]?.Value<string>() ?? "unknown"}'...");
                }

                yield return itemMessage;
            }

            PostItemMessage CreateItemActionMessage(StreamResourcePageMessage<PostItemMessage> msg, JObject j)
            {
                return new PostItemMessage
                {
                    Item = j,
                    ResourceUrl = msg.ResourceUrl,
                    PostAuthorizationFailureRetry = msg.PostAuthorizationFailureRetry,
                };
            }
        }

        private async Task<RemediationResult> TryRemediateFailureAsync(
            Func<string> javaScriptModuleFactory,
            int retryAttempt,
            EdFiApiClient targetEdFiApiClient,
            string sourceConnectionName,
            string resourceUrl,
            string sourceId,
            HttpResponseMessage responseMessage,
            string requestBody)
        {
            string remediationFunctionName = $"{resourceUrl}/{(int)responseMessage.StatusCode}";

            try
            {
                var remediationPlanContent = await _nodeJsService.InvokeFromStringAsync<string>(
                    javaScriptModuleFactory,
                    "RemediationsModule",
                    remediationFunctionName,
                    new[]
                    {
                        new FailureContext()
                        {
                            resourceUrl = resourceUrl,
                            requestBody = requestBody,
                            responseBody = await responseMessage.Content.ReadAsStringAsync(),
                            responseStatusCode = (int)responseMessage.StatusCode,
                            targetConnectionName = targetEdFiApiClient.ConnectionDetails.Name,
                            sourceConnectionName = sourceConnectionName,
                        }
                    });

                var remediationPlan = JsonSerializer.Deserialize<RemediationPlan>(remediationPlanContent);

                var modifiedRequestBody = remediationPlan?.modifiedRequestBody;

                var remediationResult = modifiedRequestBody?.ValueKind == JsonValueKind.Null
                    ? RemediationResult.Found
                    : new RemediationResult(modifiedRequestBody);

                var remediationPlanAdditionalRequests =
                    remediationPlan?.additionalRequests ?? Array.Empty<RemediationPlan.RemediationRequest>();

                if (!remediationPlanAdditionalRequests.Any())
                {
                    if (_logger.IsDebugEnabled)
                    {
                        _logger.Debug(
                            $"{resourceUrl} (source id: {sourceId}): Remediation plan for '{responseMessage.StatusCode}' did not return any additional remediation requests.");
                    }

                    return remediationResult;
                }

                // Perform the additional remediation requests
                foreach (var remediationRequest in remediationPlanAdditionalRequests)
                {
                    var remediationRequestBodyJson = (string)remediationRequest.body.ToString();

                    if (_logger.IsDebugEnabled)
                    {
                        _logger.Debug(
                            $"{resourceUrl} (source id: {sourceId}): Remediating request with POST request to '{remediationRequest.resource}' on target API: {remediationRequestBodyJson}");
                    }

                    var remediationResponse = await RequestHelpers.SendPostRequestAsync(
                        targetEdFiApiClient,
                        remediationRequest.resource,
                        $"{targetEdFiApiClient.DataManagementApiSegment}{remediationRequest.resource}",
                        remediationRequestBodyJson,
                        CancellationToken.None);
                    
                    if (remediationResponse.IsSuccessStatusCode)
                    {
                        _logger.Info(
                            $"{resourceUrl} (source id: {sourceId}): Remediation for retry attempt {retryAttempt} with POST request to '{remediationRequest.resource}' on target API succeeded with status '{remediationResponse.StatusCode}'.");
                    }
                    else
                    {
                        _logger.Warn(
                            $"{resourceUrl} (source id: {sourceId}): Remediation for retry attempt {retryAttempt} with POST request to '{remediationRequest.resource}' on target API failed with status '{remediationResponse.StatusCode}'.");
                    }
                }

                return remediationResult;
            }
            catch (InvocationException ex)
            {
                if (!ex.Message.Contains("has no export named"))
                {
                    _logger.Warn($"{resourceUrl} (source id: {sourceId}): Error occurred during remediation invocation: {ex}");
                }
                else
                {
                    _logger.Debug(
                        $"{resourceUrl} (source id: {sourceId}): No remediation found for status code '{responseMessage.StatusCode}'.");
                }

                return RemediationResult.NotFound;
            }
        }
    }

    public class RemediationResult
    {
        public static RemediationResult Found = new(true);
        public static RemediationResult NotFound = new(false);

        private RemediationResult(bool foundRemediation)
        {
            FoundRemediation = foundRemediation;
        }

        public RemediationResult(dynamic modifiedRequestBody)
        {
            FoundRemediation = true;
            ModifiedRequestBody = modifiedRequestBody;
        }

        public bool FoundRemediation { get; }

        public dynamic? ModifiedRequestBody { get; }
    }

    public class FailureContext
    {
        public string resourceUrl { get; set; }

        public string requestBody { get; set; }

        public int responseStatusCode { get; set; }

        public string responseBody { get; set; }

        public string sourceConnectionName { get; set; }

        public string targetConnectionName { get; set; }
    }

    public class RemediationPlan
    {
        public dynamic? modifiedRequestBody { get; set; }

        public RemediationRequest[]? additionalRequests { get; set; }

        public class RemediationRequest
        {
            public string resource { get; set; }

            public dynamic body { get; set; }
        }
    }
}
