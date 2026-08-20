// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Connections.Api.Helpers;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Extensions;
using EdFi.Tools.ApiPublisher.Core.Helpers;
using EdFi.Tools.ApiPublisher.Core.Processing.Blocks;
using EdFi.Tools.ApiPublisher.Core.Processing.Handlers;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using Newtonsoft.Json;
using Polly;
using Polly.Contrib.WaitAndRetry;
using Polly.RateLimiting;
using System.Threading.RateLimiting;
using Serilog;
using Serilog.Events;
using System.Threading.Tasks.Dataflow;
using Polly.Retry;
using Polly.RateLimit;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.MessageHandlers;

public class EdFiApiStreamResourcePageMessageHandler : IStreamResourcePageMessageHandler
{
    private readonly ILogger _logger = Log.ForContext(typeof(EdFiApiStreamResourcePageMessageHandler));
    private readonly ISourceEdFiApiClientProvider _sourceEdFiApiClientProvider;
    private readonly IRateLimiting<HttpResponseMessage> _rateLimiter;

    public EdFiApiStreamResourcePageMessageHandler(
        ISourceEdFiApiClientProvider sourceEdFiApiClientProvider, IRateLimiting<HttpResponseMessage> rateLimiter = null)
    {
        _sourceEdFiApiClientProvider = sourceEdFiApiClientProvider;
        _rateLimiter = rateLimiter;
    }

    public async Task<IEnumerable<TProcessDataMessage>> HandleStreamResourcePageAsync<TProcessDataMessage>(
        StreamResourcePageMessage<TProcessDataMessage> message,
        Options options,
        ITargetBlock<ErrorItemMessage> errorHandlingBlock)
    {
        long offset = message.Offset ?? throw new NullReferenceException("Offset is expected on resource page messages for the Ed-Fi ODS API.");
        int limit = message.Limit ?? throw new NullReferenceException("Limit is expected on resource page messages for the Ed-Fi ODS API.");

        var edFiApiClient = _sourceEdFiApiClientProvider.GetApiClient();

        string changeWindowQueryStringParameters = ApiRequestHelper.GetChangeWindowQueryStringParameters(message.ChangeWindow);

        try
        {
            var transformedMessages = new List<TProcessDataMessage>();

            do
            {
                if (message.CancellationSource.IsCancellationRequested)
                {
                    _logger.Debug(
                        "{MessageResourceUrl}: Cancellation requested while processing page of source items starting at offset {Offset}.",
                        message.ResourceUrl, offset);

                    return Enumerable.Empty<TProcessDataMessage>();
                }

                if (_logger.IsEnabled(LogEventLevel.Debug))
                {
                    _logger.Debug(
                        "{MessageResourceUrl}: Retrieving page items {Offset} to {OffsetLimitMinus1}.",
                        message.ResourceUrl, offset, offset + limit - 1);
                }

                var delay = Backoff.ExponentialBackoff(
                    TimeSpan.FromMilliseconds(options.RetryStartingDelayMilliseconds),
                    options.MaxRetryAttempts);

                int attempts = 0;
                // Rate Limit
                bool isRateLimitingEnabled = options.EnableRateLimit;

                var retryPolicy = Policy
                    .HandleResult<HttpResponseMessage>(r => r.StatusCode.IsPotentiallyTransientFailure())
                    .WaitAndRetryAsync(
                        delay,
                        (result, ts, retryAttempt, ctx) =>
                        {
                            _logger.Warning("{ResourceUrl}: Retrying GET page items {Offset} to {OffsetPlusLimitMinus1} from source failed with status '{StatusCode}'. Retrying... (retry #{RetryAttempt} of {MaxRetryAttempts} with {TotalSeconds:N1}s delay)",
                                message.ResourceUrl, offset, offset + limit - 1, result.Result.StatusCode, retryAttempt, options.MaxRetryAttempts, ts.TotalSeconds);

                            // With ResponseHeadersRead (see APIPUB-134), an abandoned response pins a
                            // connection until finalized -- release the transient failure being retried
                            result.Result?.Dispose();
                        });
                IAsyncPolicy<HttpResponseMessage> policy = isRateLimitingEnabled ? Policy.WrapAsync(_rateLimiter?.GetRateLimitingPolicy(), retryPolicy) : retryPolicy;
                try
                {
                    // Dispose explicitly after parsing: with ResponseHeadersRead (see APIPUB-134) an open
                    // response holds a live connection and its unread body
                    using var apiResponse = await policy.ExecuteAsync(
                            (ctx, ct) =>
                            {
                                attempts++;

                                if (attempts > 1 && _logger.IsEnabled(LogEventLevel.Debug))
                                {
                                    _logger.Debug("{ResourceUrl}: GET page items {Offset} to {OffsetPlusLimitMinus1} from source attempt #{Attempts}.",
                                        message.ResourceUrl, offset, offset + limit - 1, attempts);
                                }

                                // Possible seam for getting a page of data (here, using Ed-Fi ODS API w/ offset/limit paging strategy)
                                string requestUri =
                                    $"{edFiApiClient.DataManagementApiSegment}{message.ResourceUrl}?offset={offset}&limit={limit}{changeWindowQueryStringParameters}";

                                return RequestHelpers.SendGetRequestAsync(edFiApiClient, message.ResourceUrl, requestUri, ct);
                            },
                            new Context(),
                            CancellationToken.None);

                    // Detect null content and provide a better error message (which happens only during unit testing if mocked requests aren't properly defined)
                    if (apiResponse.Content == null)
                    {
                        throw new NullReferenceException(
                            $"Content of response for '{edFiApiClient.HttpClient.BaseAddress}{edFiApiClient.DataManagementApiSegment}{message.ResourceUrl}?offset={offset}&limit={limit}{changeWindowQueryStringParameters}' was null.");
                    }

                    // Failure
                    if (!apiResponse.IsSuccessStatusCode)
                    {
                        // Error bodies are small, so buffering them as a string is deliberate (see APIPUB-134)
                        string errorContent = await apiResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

                        var error = new ErrorItemMessage
                        {
                            Method = HttpMethod.Get.ToString(),
                            ResourceUrl = $"{edFiApiClient.DataManagementApiSegment}{message.ResourceUrl}",
                            Id = null,
                            Body = null,
                            ResponseStatus = apiResponse.StatusCode,
                            ResponseContent = errorContent
                        };

                        // Publish the failure
                        await errorHandlingBlock.SendErrorAsync(error, message.CancellationSource.Token).ConfigureAwait(false);

                        _logger.Error("{ResourceUrl}: GET page items failed with response status '{StatusCode}'.",
                            message.ResourceUrl, apiResponse.StatusCode);

                        break;
                    }

                    // Success
                    if (_logger.IsEnabled(LogEventLevel.Information) && attempts > 1)
                    {
                        _logger.Information("{ResourceUrl}: GET page items {Offset} to {OffsetPlusLimitMinus1} attempt #{Attempts} returned {StatusCode}.",
                            message.ResourceUrl, offset, offset + limit - 1, attempts, apiResponse.StatusCode);
                    }

                    // Transform the page content to item actions, streaming the response body in a single
                    // forward-only pass -- the page is never buffered as a whole string (see APIPUB-134)
                    int? topLevelItemCount = null;
                    var pageMessages = new List<TProcessDataMessage>();

                    try
                    {
                        await using var responseStream = await apiResponse.Content.ReadAsStreamAsync().ConfigureAwait(false);

                        // JSON is UTF-8 per RFC 8259 (and the Ed-Fi ODS API always emits UTF-8); StreamReader's
                        // default UTF-8-with-BOM-detection deliberately replaces ReadAsStringAsync's charset negotiation
                        using var streamReader = new StreamReader(responseStream);

                        // Drain into a page-local list so a mid-page parse failure contributes no messages
                        // (matching the previous whole-page JArray.Parse semantics)
                        pageMessages.AddRange(
                            message.CreateProcessDataMessages(message, streamReader, count => topLevelItemCount = count));
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
                            // The page was streamed, not buffered, so the body is no longer in hand;
                            // the exception carries the parse position (line/position/path) instead
                            ResponseContent = null,
                            Exception = ex,
                        };

                        // Publish the failure
                        await errorHandlingBlock.SendErrorAsync(error, message.CancellationSource.Token).ConfigureAwait(false);

                        _logger.Error(ex,
                            "{ResourceUrl}: JSON parsing of source page data failed at offset {Offset} (limit {Limit}).",
                            message.ResourceUrl, offset, limit);

                        break;
                    }

                    transformedMessages.AddRange(pageMessages);

                    if (!options.UseReversePaging)
                    {
                        // Perform limit/offset final page check (for need for possible continuation)
                        // (Item count was captured during the single streaming pass over the page -- a count
                        // is never reported when item creation stopped early alongside cancellation, and no
                        // count means no continuation)
                        if (message.IsFinalPage && topLevelItemCount == limit)
                        {
                            if (_logger.IsEnabled(LogEventLevel.Debug))
                            {
                                _logger.Debug("{ResourceUrl}: Final page was full. Attempting to retrieve more data.",
                                    message.ResourceUrl);
                            }

                            // Looks like there could be more data
                            offset += limit;

                            continue;
                        }
                    }
                    else
                    {
                        break;
                    }
                }
                catch (RateLimitRejectedException ex)
                {
                    _logger.Fatal(ex, "{ResourceUrl}: Rate limit exceeded. Please try again later.",
                        message.ResourceUrl);
                }
                break;
            }
            while (true);

            return transformedMessages;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{ResourceUrl}: {Ex}", message.ResourceUrl, ex);

            // An error occurred while parsing the JSON
            var error = new ErrorItemMessage
            {
                Method = HttpMethod.Get.ToString(),
                ResourceUrl = $"{edFiApiClient.DataManagementApiSegment}{message.ResourceUrl}",
                Exception = ex,
            };

            // Publish the failure
            await errorHandlingBlock.SendErrorAsync(error, message.CancellationSource.Token).ConfigureAwait(false);

            return Array.Empty<TProcessDataMessage>();
        }
    }
}
