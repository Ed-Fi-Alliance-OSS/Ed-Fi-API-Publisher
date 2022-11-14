// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using EdFi.Tools.ApiPublisher.Core.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Extensions;
using EdFi.Tools.ApiPublisher.Core.Helpers;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using log4net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Polly;
using Polly.Contrib.WaitAndRetry;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Handlers;

public class EdFiOdsApiStreamResourcePageMessageHandler : IStreamResourcePageMessageHandler
{
    private readonly ILog _logger = LogManager.GetLogger(typeof(EdFiOdsApiStreamResourcePageMessageHandler));
    private readonly ISourceEdFiApiClientProvider _sourceEdFiApiClientProvider;
    private readonly IItemActionMessageProducer _itemActionMessageProducer;

    public EdFiOdsApiStreamResourcePageMessageHandler(ISourceEdFiApiClientProvider sourceEdFiApiClientProvider, IItemActionMessageProducer itemActionMessageProducer)
    {
        _sourceEdFiApiClientProvider = sourceEdFiApiClientProvider;
        _itemActionMessageProducer = itemActionMessageProducer;
    }

    public async Task<IEnumerable<TItemActionMessage>> HandleStreamResourcePageAsync<TItemActionMessage>(
        StreamResourcePageMessage<TItemActionMessage> message,
        Options options,
        ITargetBlock<ErrorItemMessage> errorHandlingBlock)
    {
        long offset = message.Offset;
        int limit = message.Limit;

        var edFiApiClient = _sourceEdFiApiClientProvider.GetApiClient();

        string changeWindowQueryStringParameters = ApiRequestHelper.GetChangeWindowQueryStringParameters(message.ChangeWindow);

        try
        {
            var transformedMessages = new List<TItemActionMessage>();

            do
            {
                if (message.CancellationSource.IsCancellationRequested)
                {
                    _logger.Debug(
                        $"{message.ResourceUrl}: Cancellation requested while processing page of source items starting at offset {offset}.");

                    return Enumerable.Empty<TItemActionMessage>();
                }

                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"{message.ResourceUrl}: Retrieving page items {offset} to {offset + limit - 1}.");
                }

                var delay = Backoff.ExponentialBackoff(
                    TimeSpan.FromMilliseconds(options.RetryStartingDelayMilliseconds),
                    options.MaxRetryAttempts);

                int attempts = 0;

                var apiResponse = await Policy
                    .HandleResult<HttpResponseMessage>(r => r.StatusCode.IsPotentiallyTransientFailure())
                    .WaitAndRetryAsync(
                        delay,
                        (result, ts, retryAttempt, ctx) =>
                        {
                            _logger.Warn(
                                $"{message.ResourceUrl}: Retrying GET page items {offset} to {offset + limit - 1} from source failed with status '{result.Result.StatusCode}'. Retrying... (retry #{retryAttempt} of {options.MaxRetryAttempts} with {ts.TotalSeconds:N1}s delay)");
                        })
                    .ExecuteAsync(
                        (ctx, ct) =>
                        {
                            attempts++;

                            if (attempts > 1)
                            {
                                if (_logger.IsDebugEnabled)
                                {
                                    _logger.Debug(
                                        $"{message.ResourceUrl}: GET page items {offset} to {offset + limit - 1} from source attempt #{attempts}.");
                                }
                            }

                            // Possible seam for getting a page of data (here, using Ed-Fi ODS API w/ offset/limit paging strategy)
                            return edFiApiClient.HttpClient.GetAsync(
                                $"{edFiApiClient.DataManagementApiSegment}{message.ResourceUrl}?offset={offset}&limit={limit}{changeWindowQueryStringParameters}",
                                ct);
                        },
                        new Context(),
                        CancellationToken.None);

                // Detect null content and provide a better error message (which happens only during unit testing if mocked requests aren't properly defined)
                if (apiResponse.Content == null)
                {
                    throw new NullReferenceException(
                        $"Content of response for '{edFiApiClient.HttpClient.BaseAddress}{edFiApiClient.DataManagementApiSegment}{message.ResourceUrl}?offset={offset}&limit={limit}{changeWindowQueryStringParameters}' was null.");
                }

                string responseContent = await apiResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

                // Failure
                if (!apiResponse.IsSuccessStatusCode)
                {
                    var error = new ErrorItemMessage
                    {
                        Method = HttpMethod.Get.ToString(),
                        ResourceUrl = $"{edFiApiClient.DataManagementApiSegment}{message.ResourceUrl}",
                        Id = null,
                        Body = null,
                        ResponseStatus = apiResponse.StatusCode,
                        ResponseContent = responseContent
                    };

                    // Publish the failure
                    errorHandlingBlock.Post(error);

                    break;
                }

                // Success
                if (_logger.IsInfoEnabled && attempts > 1)
                {
                    _logger.Info(
                        $"{message.ResourceUrl}: GET page items {offset} to {offset + limit - 1} attempt #{attempts} returned {apiResponse.StatusCode}.");
                }

                // Transform the page content to item actions
                try
                {
                    transformedMessages.AddRange(_itemActionMessageProducer.ProduceMessages(responseContent, message));
                }
                catch (JsonReaderException ex)
                {
                    // An error occurred while parsing the JSON
                    var error = new ErrorItemMessage
                    {
                        Method = HttpMethod.Get.ToString(),
                        ResourceUrl = $"{edFiApiClient.DataManagementApiSegment}{message.ResourceUrl}",
                        Id = null,
                        Body = null,
                        ResponseStatus = apiResponse.StatusCode,
                        ResponseContent = responseContent
                    };

                    // Publish the failure
                    errorHandlingBlock.Post(error);

                    _logger.Error(
                        $"{message.ResourceUrl}: JSON parsing of source page data failed: {ex}{Environment.NewLine}{responseContent}");

                    throw new Exception("JSON parsing of source page data failed.", ex);
                }

                // Perform limit/offset final page check (for need for possible continuation)
                if (message.IsFinalPage && JArray.Parse(responseContent).Count == limit)
                {
                    if (_logger.IsDebugEnabled)
                    {
                        _logger.Debug($"{message.ResourceUrl}: Final page was full. Attempting to retrieve more data.");
                    }

                    // Looks like there could be more data
                    offset += limit;

                    continue;
                }

                break;
            }
            while (true);

            return transformedMessages;
        }
        catch (Exception ex)
        {
            _logger.Error($"{message.ResourceUrl}: {ex}");

            return Array.Empty<TItemActionMessage>();
        }
    }
}