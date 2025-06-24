// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks.Dataflow;
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
using Newtonsoft.Json.Linq;
using Polly;
using Polly.Contrib.WaitAndRetry;
using Polly.RateLimit;
using Serilog;
using Serilog.Events;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Blocks
{
    public class PostResourceProcessingBlocksFactory : IProcessingBlocksFactory<PostItemMessage>
    {
        private readonly ILogger _logger = Log.ForContext(typeof(PostResourceProcessingBlocksFactory));
        private readonly INodeJSService _nodeJsService;
        private readonly ITargetEdFiApiClientProvider _targetEdFiApiClientProvider;
        private readonly ISourceConnectionDetails _sourceConnectionDetails;
        private readonly ISourceCapabilities _sourceCapabilities;
        private readonly ISourceResourceItemProvider _sourceResourceItemProvider;
        private readonly IRateLimiting<HttpResponseMessage> _rateLimiter;

        public PostResourceProcessingBlocksFactory(
            INodeJSService nodeJsService,
            ITargetEdFiApiClientProvider targetEdFiApiClientProvider,
            ISourceConnectionDetails sourceConnectionDetails,
            ISourceCapabilities sourceCapabilities,
            ISourceResourceItemProvider sourceResourceItemProvider,
            IRateLimiting<HttpResponseMessage> rateLimiter = null
        )
        {
            _nodeJsService = nodeJsService;
            _targetEdFiApiClientProvider = targetEdFiApiClientProvider;
            _sourceConnectionDetails = sourceConnectionDetails;
            _sourceCapabilities = sourceCapabilities;
            _sourceResourceItemProvider = sourceResourceItemProvider;
            _rateLimiter = rateLimiter;

            // Ensure that the API connections are configured correctly with regards to Profiles
            // If we have no Profile applied to the target... ensure that the source also has no profile specified (to prevent accidental data loss on POST)
            if (string.IsNullOrEmpty(targetEdFiApiClientProvider.GetApiClient().ConnectionDetails.ProfileName)

                // If the source is an API
                && (sourceConnectionDetails is ApiConnectionDetails sourceApiConnectionDetails)

                // If the source API is not using a Profile, prevent processing by throwing an exception
                && (!string.IsNullOrEmpty(sourceApiConnectionDetails.ProfileName)))
            {
                throw new Exception(
                    "The source API connection has a ProfileName specified, but the target API connection does not. POST requests against a target API without the Profile-based context of the source data can lead to accidental data loss.");
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

        private async Task<IEnumerable<ErrorItemMessage>> HandlePostItemMessage(
            ConcurrentDictionary<string, bool> ignoredResourceByUrl,
            PostItemMessage postItemMessage,
            Options options,
            Func<string> javaScriptModuleFactory,
            EdFiApiClient targetEdFiApiClient,
            HashSet<(string resourceUrl, HttpStatusCode statusCode)> knownUnremediatedRequests,
            Dictionary<string, string> missingDependencyByResourcePath)
        {
            if (ignoredResourceByUrl.ContainsKey(postItemMessage.ResourceUrl))
            {
                return Enumerable.Empty<ErrorItemMessage>();
            }

            var idToken = postItemMessage.Item["id"];
            string id = idToken.Value<string>();

            try
            {
                if (_logger.IsEnabled(LogEventLevel.Debug))
                {
                    _logger.Debug("{ResourceUrl} (source id: {Id}): Processing PostItemMessage (with up to {MaxRetryAttempts} retries).",
                        postItemMessage.ResourceUrl, id, options.MaxRetryAttempts);
                }

                // Remove attributes not usable between API instances
                postItemMessage.Item.Remove("id");
                postItemMessage.Item.Remove("_etag");
                postItemMessage.Item.Remove("_lastModifiedDate");

                // For descriptors, also strip the surrogate id
                if (postItemMessage.ResourceUrl.EndsWith("Descriptors"))
                {
                    string[] descriptorBaseNameSplit = postItemMessage.ResourceUrl.Split('/');
                    string descriptorBaseName = descriptorBaseNameSplit[descriptorBaseNameSplit.Length - 1].TrimEnd('s');
                    string descriptorIdPropertyName = $"{descriptorBaseName}Id";

                    postItemMessage.Item.Remove(descriptorIdPropertyName);
                }

                var delay = Backoff.ExponentialBackoff(
                    TimeSpan.FromMilliseconds(options.RetryStartingDelayMilliseconds),
                    options.MaxRetryAttempts);

                int attempts = 0;
                // Rate Limit
                bool isRateLimitingEnabled = options.EnableRateLimit;
                var retryPolicy = Policy.Handle<Exception>()
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
                            if (result.Exception != null)
                            {
                                _logger.Warning(result.Exception, "{ResourceUrl} (source id: {Id}): POST attempt #{Attempts} failed with an exception. Retrying... (retry #{RetryAttempt} of {MaxRetryAttempts} with {TotalSeconds:N1}s delay):{NewLine}{Exception}",
                                    postItemMessage.ResourceUrl, id, attempts, retryAttempt, options.MaxRetryAttempts, ts.TotalSeconds, Environment.NewLine, result.Exception);
                            }
                            else
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
                                        if (_logger.IsEnabled(LogEventLevel.Debug))
                                        {
                                            string modifiedRequestBodyJson = JsonSerializer.Serialize(
                                                remediationResult.ModifiedRequestBody,
                                                new JsonSerializerOptions { WriteIndented = true });

                                            _logger.Debug("{ResourceUrl} (source id: {Id}): Remediation plan provided a modified request body: {ModifiedRequestBodyJson}",
                                                postItemMessage.ResourceUrl,
                                                id,
                                                modifiedRequestBodyJson);
                                        }

                                        ctx["ModifiedRequestBody"] = remediationResult.ModifiedRequestBody;
                                    }
                                }
                                string responseContent = await result.Result.Content.ReadAsStringAsync().ConfigureAwait(false);

                                var message = $"{postItemMessage.ResourceUrl} (source id: {id}): POST attempt #{attempts} failed with status '{result.Result.StatusCode}'. Retrying... (retry #{retryAttempt} of {options.MaxRetryAttempts} with {ts.TotalSeconds:N1}s delay):{Environment.NewLine}{responseContent}";
                                _logger.Warning(message);
                            }
                        });
                IAsyncPolicy<HttpResponseMessage> policy = isRateLimitingEnabled ? Policy.WrapAsync(_rateLimiter?.GetRateLimitingPolicy(), retryPolicy) : retryPolicy;
                var apiResponse = await policy.ExecuteAsync(
                async (ctx, ct) =>
                {
                    attempts++;

                    if (_logger.IsEnabled(LogEventLevel.Debug))
                    {
                        if (attempts > 1)
                        {
                            _logger.Debug("{ResourceUrl} (source id: {Id}): POST attempt #{Attempts}.", postItemMessage.ResourceUrl, id, attempts);
                        }
                        else
                        {
                            _logger.Debug("{ResourceUrl} (source id: {Id}): Sending POST request.", postItemMessage.ResourceUrl, id);
                        }
                    }

                    // Prepare request body
                    string requestBodyJson;

                    if (ctx.TryGetValue("ModifiedRequestBody", out dynamic modifiedRequestBody))
                    {
                        _logger.Information("{ResourceUrl} (source id: {Id}): Applying modified request body from remediation plan...", postItemMessage.ResourceUrl, id);

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

                    var (hasMissingDependency, missingDependencyDetails) = await TryGetMissingDependencyDetailsAsync(response, postItemMessage);

                    if (hasMissingDependency)
                    {
                        if (!_sourceCapabilities.SupportsGetItemById)
                        {
                            _logger.Warning("{ResourceUrl}: Reference '{ReferenceName}' to resource '{ReferencedResourceName}' could not be automatically resolved because the source connection does not support retrieving items by id.",
                                postItemMessage.ResourceUrl, missingDependencyDetails!.ReferenceName, missingDependencyDetails.ReferencedResourceName);

                            return response;
                        }

                        _logger.Information("{ResourceUrl}: Attempting to retrieve missing '{ReferencedResourceName}' reference based on 'authorizationFailureHandling' metadata in apiPublisherSettings.json.",
                            postItemMessage.ResourceUrl, missingDependencyDetails.ReferencedResourceName);

                        var (missingDependencyItemRetrieved, missingItemJson) = await _sourceResourceItemProvider.TryGetResourceItemAsync(missingDependencyDetails.SourceDependencyItemUrl);

                        if (missingDependencyItemRetrieved)
                        {
                            var missingItem = JObject.Parse(missingItemJson!);

                            var postDependencyItemMessage = new PostItemMessage
                            {
                                ResourceUrl = missingDependencyDetails.DependencyResourceUrl,
                                Item = missingItem,
                                PostAuthorizationFailureRetry = postItemMessage.PostAuthorizationFailureRetry, // TODO: Is this appropriate to copy?
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
                        if (_logger.IsEnabled(LogEventLevel.Debug))
                        {
                            _logger.Debug("{ResourceUrl} (source id: {Id}): POST returned {Conflict}, but for descriptors this means the value is already present.",
                                postItemMessage.ResourceUrl, id, HttpStatusCode.Conflict);
                        }

                        return Enumerable.Empty<ErrorItemMessage>();
                    }

                    // Gracefully handle authorization errors by using the retry action delegate
                    // (if present) to post the message to the retry "resource" queue 
                    if (apiResponse.StatusCode == HttpStatusCode.Forbidden
                        // Determine if current resource has an authorization retry queue
                        && postItemMessage.PostAuthorizationFailureRetry != null)
                    {
                        if (_logger.IsEnabled(LogEventLevel.Debug))
                        {
                            _logger.Debug("{ResourceUrl} (source id: {Id}): Authorization failed -- deferring for retry after pertinent associations are processed.",
                                postItemMessage.ResourceUrl, id);
                        }

                        // Re-add the identifier, and pass the message along to the "retry" resource (after associations have been processed)
                        postItemMessage.Item.Add("id", idToken);
                        postItemMessage.PostAuthorizationFailureRetry(postItemMessage);

                        // Deferring for retry - no errors to publish
                        return Enumerable.Empty<ErrorItemMessage>();
                    }

                    string responseContent = await apiResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var message = string.Empty;

                    // If the failure is Forbidden, and we should treat it as a warning
                    if (apiResponse.StatusCode == HttpStatusCode.Forbidden
                        && postItemMessage.PostAuthorizationFailureRetry == null
                        && targetEdFiApiClient.ConnectionDetails?.TreatForbiddenPostAsWarning == true)
                    {
                        // Warn and ignore all future data for this resource
                        message = $"{postItemMessage.ResourceUrl} (source id: {id}): Authorization failed on POST of resource with no authorization failure handling defined. Remaining resource items will be ignored. Response status: {apiResponse.StatusCode}{Environment.NewLine}{responseContent}";
                        _logger.Warning(message);

                        ignoredResourceByUrl.TryAdd(postItemMessage.ResourceUrl, true);

                        return Enumerable.Empty<ErrorItemMessage>();
                    }

                    // Error is final, log it and indicate failure for processing
                    message = $"{postItemMessage.ResourceUrl} (source id: {id}): POST attempt #{attempts} failed with status '{apiResponse.StatusCode}':{Environment.NewLine}{responseContent}";
                    _logger.Error(message);

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
                    if (_logger.IsEnabled(LogEventLevel.Information))
                    {
                        _logger.Information("{ResourceUrl} (source id: {Id}): POST attempt #{Attempts} returned {StatusCode}.",
                            postItemMessage.ResourceUrl, id, attempts, apiResponse.StatusCode);
                    }
                }
                else
                {
                    // Ensure a log entry when POST succeeds on first attempt and DEBUG logging is enabled
                    if (_logger.IsEnabled(LogEventLevel.Debug))
                    {
                        _logger.Debug("{ResourceUrl} (source id: {Id}): POST attempt #{Attempts} returned {StatusCode}.",
                            postItemMessage.ResourceUrl, id, attempts, apiResponse.StatusCode);
                    }
                }

                // Success - no errors to publish
                return Enumerable.Empty<ErrorItemMessage>();
            }
#pragma warning disable S2139
            catch (RateLimitRejectedException ex)
            {
                _logger.Fatal(ex, "{ResourceUrl}: Rate limit exceeded. Please try again later.", postItemMessage.ResourceUrl);
                return Enumerable.Empty<ErrorItemMessage>();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "{ResourceUrl} (source id: {Id}): An unhandled exception occurred in the PostResource block: {Ex}", postItemMessage.ResourceUrl, id, ex);
                throw;
            }
#pragma warning restore S2139
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
                if (postItemResponse.StatusCode == HttpStatusCode.BadRequest
                    // If resource is a "primary relationship" configured in authorization failure handling
                    && missingDependencyByResourcePath.TryGetValue(msg.ResourceUrl, out string missingDependencyResourcePath))
                {
                    string responseMessageText = GetResponseMessageText(postItemResponse);

                    if (responseMessageText?.Contains("reference could not be resolved.") == true)
                    {
                        return true;
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

            async Task<(bool success, MissingDependencyDetails)> TryGetMissingDependencyDetailsAsync(HttpResponseMessage postItemResponse, PostItemMessage msg)
            {
                // If response is a Bad Request (which is the API's error response for missing Staff/Student/Parent), check for need to explicitly fetch dependencies
                // NOTE: If support is expanded for other missing dependencies, the response code from the API (currently) will be a 409 Conflict status.
                if (postItemResponse.StatusCode == HttpStatusCode.BadRequest
                    // If resource is a "primary relationship" configured in authorization failure handling
                    && missingDependencyByResourcePath.TryGetValue(msg.ResourceUrl, out string missingDependencyResourcePath))
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
                            string dependencyItemUrl = msg.Item.SelectToken($"{referenceName}.link.href")?.Value<string>();

                            if (dependencyItemUrl == null)
                            {
                                _logger.Warning("{ResourceUrl}: Unable to extract href to '{ReferenceName}' from JSON body for obtaining missing dependency.",
                                    msg.ResourceUrl, referenceName);
                                return (false, null);
                            }

                            // URL is expected to be of the format of 
                            var parts = dependencyItemUrl.Split('/');

                            if (parts.Length < 3)
                            {
                                _logger.Warning("{ResourceUrl}: Unable to identify missing dependency resource URL from the supplied dependency item URL '{DependencyItemUrl}'.",
                                    msg.ResourceUrl, dependencyItemUrl);
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
                                _logger.Warning(ex, "{ResourceUrl}: Unable to identify missing dependency resource URL from the supplied dependency item URL '{DependencyItemUrl}'.",
                                    msg.ResourceUrl, dependencyItemUrl);
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
        private sealed record MissingDependencyDetails
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
                    _logger.Debug("{ResourceUrl}: Cancellation requested during item '{NameofPostItemMessage}' creation.", message.ResourceUrl, nameof(PostItemMessage));

                    yield break;
                }

                // Add the item to the buffer for processing into the target API
                if (_logger.IsEnabled(LogEventLevel.Debug))
                {
                    _logger.Debug("{ResourceUrl}: Adding individual action message of type '{NameofPostItemMessage}' for item '{ItemId}'...",
                        message.ResourceUrl, nameof(PostItemMessage), item["id"]?.Value<string>() ?? "unknown");
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
                    if (_logger.IsEnabled(LogEventLevel.Debug))
                    {
                        _logger.Debug("{ResourceUrl} (source id: {SourceId}): Remediation plan for '{StatusCode}' did not return any additional remediation requests.",
                            resourceUrl, sourceId, responseMessage.StatusCode);
                    }

                    return remediationResult;
                }

                // Perform the additional remediation requests
                foreach (var remediationRequest in remediationPlanAdditionalRequests)
                {
                    var remediationRequestBodyJson = (string)remediationRequest.body.ToString();

                    if (_logger.IsEnabled(LogEventLevel.Debug))
                    {
                        var message = $"{resourceUrl} (source id: {sourceId}): Remediating request with POST request to '{remediationRequest.resource}' on target API: {remediationRequestBodyJson}";
                        _logger.Debug(message);
                    }

                    var remediationResponse = await RequestHelpers.SendPostRequestAsync(
                        targetEdFiApiClient,
                        remediationRequest.resource,
                        $"{targetEdFiApiClient.DataManagementApiSegment}{remediationRequest.resource}",
                        remediationRequestBodyJson,
                        CancellationToken.None);

                    if (remediationResponse.IsSuccessStatusCode)
                    {
                        _logger.Information("{ResourceUrl} (source id: {SourceId}): Remediation for retry attempt {RetryAttempt} with POST request to '{RemediationRequestResource}' on target API succeeded with status '{RemediationResponseStatusCode}'.",
                            resourceUrl, sourceId, retryAttempt, remediationRequest.resource, remediationResponse.StatusCode);
                    }
                    else
                    {
                        _logger.Warning("{ResourceUrl} (source id: {SourceId}): Remediation for retry attempt {RetryAttempt} with POST request to '{RemediationRequestResource}' on target API failed with status '{RemediationResponseStatusCode}'.",
                            resourceUrl, sourceId, retryAttempt, remediationRequest.resource, remediationResponse.StatusCode);
                    }
                }

                return remediationResult;
            }
            catch (InvocationException ex)
            {
                if (!ex.Message.Contains("has no export named"))
                {
                    _logger.Warning(ex, "{ResourceUrl} (source id: {SourceId}): Error occurred during remediation invocation: {Ex}", resourceUrl, sourceId, ex);
                }
                else
                {
                    _logger.Debug(ex, "{ResourceUrl} (source id: {SourceId}): No remediation found for status code '{ResponseMessageStatusCode}'.",
                        resourceUrl, sourceId, responseMessage.StatusCode);
                }

                return RemediationResult.NotFound;
            }
        }
    }

    public class RemediationResult
    {
        public static RemediationResult Found { get; set; } = new(true);
        public static RemediationResult NotFound { get; set; } = new(false);

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

        public dynamic ModifiedRequestBody { get; }
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
        public dynamic modifiedRequestBody { get; set; }

        public RemediationRequest[] additionalRequests { get; set; }

        public class RemediationRequest
        {
            public string resource { get; set; }

            public dynamic body { get; set; }
        }
    }
}
