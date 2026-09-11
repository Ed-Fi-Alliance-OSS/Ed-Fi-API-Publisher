// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Connections.Api.Helpers;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Paging;
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
    private readonly IPageRequestStrategy _pageRequestStrategy;
    private readonly IRateLimiting<HttpResponseMessage> _rateLimiter;

    public EdFiApiStreamResourcePageMessageHandler(
        ISourceEdFiApiClientProvider sourceEdFiApiClientProvider,
        IPageRequestStrategy pageRequestStrategy,
        IRateLimiting<HttpResponseMessage> rateLimiter = null)
    {
        _sourceEdFiApiClientProvider = sourceEdFiApiClientProvider
            ?? throw new ArgumentNullException(nameof(sourceEdFiApiClientProvider));

        _pageRequestStrategy = pageRequestStrategy
            ?? throw new ArgumentNullException(nameof(pageRequestStrategy));

        _rateLimiter = rateLimiter;
    }

    public async IAsyncEnumerable<TProcessDataMessage> HandleStreamResourcePageAsync<TProcessDataMessage>(
        StreamResourcePageMessage<TProcessDataMessage> message,
        Options options,
        ITargetBlock<ErrorItemMessage> errorHandlingBlock)
    {
        // The paging strategy addresses the successive requests for this page message and validates the
        // paging context it needs on the message (see APIPUB-138)
        var pageRequest = _pageRequestStrategy.Begin(message, options);

        var state = new PageReadState<TProcessDataMessage>(
            message,
            options,
            errorHandlingBlock,
            pageRequest,
            _sourceEdFiApiClientProvider.GetApiClient(),

            // Strategy-specific paging values (e.g. Offset and Limit) travel as structured properties on every
            // log event for the page, while the rendered messages use the strategy's descriptions (":l" keeps
            // Serilog from quoting those strings, so the text reads exactly as the numbers did before the seam)
            pageRequest.EnrichLogger(_logger),
            ApiRequestHelper.GetChangeWindowQueryStringParameters(message.ChangeWindow));

        try
        {
            // One iteration per page request. Items are yielded page by page so the consumer's pull rate, not the
            // size of the partition, bounds what is in memory (see APIPUB-112/APIPUB-139). All failure handling
            // lives in ReadNextPageAsync: an iterator cannot yield from inside a try block that has a catch.
            while (true)
            {
                var (pageItems, hasMorePages) = await ReadNextPageAsync(state).ConfigureAwait(false);

                foreach (var pageItem in pageItems)
                {
                    yield return pageItem;
                }

                if (!hasMorePages)
                {
                    yield break;
                }
            }
        }
        finally
        {
            state.BodyReadDeadline?.Dispose();
        }
    }

    /// <summary>
    /// Issues the strategy's current request (with retries), streams the page into item messages, and reports
    /// whether the strategy advanced to another request. Failures are published and end the sequence.
    /// </summary>
    private async Task<(IReadOnlyList<TProcessDataMessage> Items, bool HasMorePages)> ReadNextPageAsync<TProcessDataMessage>(
        PageReadState<TProcessDataMessage> state)
    {
        var message = state.Message;
        var options = state.Options;
        var errorHandlingBlock = state.ErrorHandlingBlock;
        var pageRequest = state.PageRequest;
        var edFiApiClient = state.EdFiApiClient;
        var pageLogger = state.PageLogger;

        var none = (Items: (IReadOnlyList<TProcessDataMessage>)Array.Empty<TProcessDataMessage>(), HasMorePages: false);

        try
        {
            if (message.CancellationSource.IsCancellationRequested)
            {
                pageLogger.Debug(
                    "{MessageResourceUrl}: Cancellation requested while processing page of source items starting at {PageStart:l}.",
                    message.ResourceUrl, pageRequest.DescribeStart());

                return none;
            }

            if (pageLogger.IsEnabled(LogEventLevel.Debug))
            {
                pageLogger.Debug(
                    "{MessageResourceUrl}: Retrieving page items {PageItems:l}.",
                    message.ResourceUrl, pageRequest.Describe());
            }

            // The strategy's current request does not change across retry attempts of the same page
            string requestUri =
                $"{edFiApiClient.DataManagementApiSegment}{message.ResourceUrl}{pageRequest.BuildQueryString()}{state.ChangeWindowQueryStringParameters}";

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
                        pageLogger.Warning("{ResourceUrl}: Retrying GET page items {PageItems:l} from source failed with status '{StatusCode}'. Retrying... (retry #{RetryAttempt} of {MaxRetryAttempts} with {TotalSeconds:N1}s delay)",
                            message.ResourceUrl, pageRequest.Describe(), result.Result.StatusCode, retryAttempt, options.MaxRetryAttempts, ts.TotalSeconds);

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

                            if (attempts > 1 && pageLogger.IsEnabled(LogEventLevel.Debug))
                            {
                                pageLogger.Debug("{ResourceUrl}: GET page items {PageItems:l} from source attempt #{Attempts}.",
                                    message.ResourceUrl, pageRequest.Describe(), attempts);
                            }

                            return RequestHelpers.SendGetRequestAsync(edFiApiClient, message.ResourceUrl, requestUri, ct);
                        },
                        new Context(),
                        message.CancellationSource.Token);

                // With ResponseHeadersRead (see APIPUB-134) HttpClient.Timeout covers only the wait for the headers,
                // so the body read gets its own deadline of the same length -- restoring the bound that applied to
                // the whole response before streaming. Linked to the resource's token so cancellation still wins.
                state.BodyReadDeadline?.Dispose();
                state.BodyReadDeadline = CancellationTokenSource.CreateLinkedTokenSource(message.CancellationSource.Token);
                state.BodyReadDeadline.CancelAfter(edFiApiClient.HttpClient.Timeout);
                var bodyReadDeadline = state.BodyReadDeadline;

                // Detect null content and provide a better error message (which happens only during unit testing if mocked requests aren't properly defined)
                if (apiResponse.Content == null)
                {
                    throw new NullReferenceException(
                        $"Content of response for '{edFiApiClient.HttpClient.BaseAddress}{requestUri}' was null.");
                }

                // Failure
                if (!apiResponse.IsSuccessStatusCode)
                {
                    // Error bodies are small, so buffering them as a string is deliberate (see APIPUB-134)
                    string errorContent = await apiResponse.Content.ReadAsStringAsync(bodyReadDeadline.Token).ConfigureAwait(false);

                    var error = new ErrorItemMessage
                    {
                        IsSourceReadError = true,
                        Method = HttpMethod.Get.ToString(),
                        ResourceUrl = $"{edFiApiClient.DataManagementApiSegment}{message.ResourceUrl}",
                        Id = null,
                        Body = null,
                        ResponseStatus = apiResponse.StatusCode,
                        ResponseContent = errorContent
                    };

                    // Publish the failure
                    await errorHandlingBlock.SendErrorAsync(error, message.CancellationSource.Token).ConfigureAwait(false);

                    pageLogger.Error("{ResourceUrl}: GET page items {PageItems:l} failed with response status '{StatusCode}'.",
                        message.ResourceUrl, pageRequest.Describe(), apiResponse.StatusCode);

                    return none;
                }

                // Success
                if (pageLogger.IsEnabled(LogEventLevel.Information) && attempts > 1)
                {
                    pageLogger.Information("{ResourceUrl}: GET page items {PageItems:l} attempt #{Attempts} returned {StatusCode}.",
                        message.ResourceUrl, pageRequest.Describe(), attempts, apiResponse.StatusCode);
                }

                // Transform the page content to item actions, streaming the response body in a single
                // forward-only pass -- the page is never buffered as a whole string (see APIPUB-134)
                int? topLevelItemCount = null;
                var pageMessages = new List<TProcessDataMessage>();

                try
                {
                    await using var responseStream = await apiResponse.Content.ReadAsStreamAsync(bodyReadDeadline.Token).ConfigureAwait(false);

                    // The parse below reads the stream synchronously, so a blocked read on a slow body would
                    // observe neither cancellation nor the deadline on its own -- registering disposal aborts
                    // the read (the resulting ObjectDisposedException/IOException is classified by the
                    // timeout-aware and cancellation-aware catches below)
                    using var abortRegistration = bodyReadDeadline.Token.Register(() => responseStream.Dispose());

                    // JSON is UTF-8 per RFC 8259 (and the Ed-Fi ODS API always emits UTF-8); StreamReader's
                    // default UTF-8-with-BOM-detection deliberately replaces ReadAsStringAsync's charset negotiation
                    using var streamReader = new StreamReader(responseStream);

                    // Drain into a page-local list so a mid-page parse failure contributes no messages
                    // (matching the previous whole-page JArray.Parse semantics)
                    pageMessages.AddRange(
                        message.CreateProcessDataMessages(message, streamReader, count => topLevelItemCount = count));
                }
                catch (JsonReaderException ex) when (!bodyReadDeadline.IsCancellationRequested)
                {
                    // An error occurred while parsing the JSON (a parse failure caused by cancellation or the
                    // body-read deadline aborting the response stream falls through to the catches below instead)
                    var error = new ErrorItemMessage
                    {
                        IsSourceReadError = true,
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

                    pageLogger.Error(ex,
                        "{ResourceUrl}: JSON parsing of source page data failed for page items {PageItems:l}.",
                        message.ResourceUrl, pageRequest.Describe());

                    return none;
                }

                // The body has been read, so disarm the deadline. Under backpressure the gap before the next
                // request is the consumer's drain time, which is unbounded (see APIPUB-139): a deadline left
                // armed would fire during that gap and the first outer catch filter below would then
                // misclassify the next page's failure -- an authentication failure above all -- as a
                // body-read timeout. Nothing after the parse uses it; the iterator's finally covers the
                // early-exit paths that return before reaching here.
                state.BodyReadDeadline.Dispose();
                state.BodyReadDeadline = null;

                // The strategy decides whether another request follows for this page message (e.g. the
                // limit/offset final page check, or the cursor Next-Page-Token header). The item count was
                // captured during the single streaming pass over the page -- a count is never reported when
                // item creation stopped early alongside cancellation.
                if (pageRequest.TryAdvance(apiResponse, topLevelItemCount))
                {
                    state.PageLogger = pageRequest.EnrichLogger(_logger);

                    return (pageMessages, true);
                }

                return (pageMessages, false);
            }
            catch (RateLimitRejectedException ex)
            {
                pageLogger.Fatal(ex, "{ResourceUrl}: Rate limit exceeded. Please try again later.",
                    message.ResourceUrl);

                // The page, and with it the rest of the message (a whole partition under cursor paging), is
                // abandoned. Published as an error so that the run cannot report success after reading only
                // part of the source (APIPUB-120).
                await errorHandlingBlock.SendErrorAsync(
                        new ErrorItemMessage
                        {
                            IsSourceReadError = true,
                            Method = HttpMethod.Get.ToString(),
                            ResourceUrl = $"{edFiApiClient.DataManagementApiSegment}{message.ResourceUrl}",
                            Exception = ex,
                        },
                        message.CancellationSource.Token)
                    .ConfigureAwait(false);

                return none;
            }
        }
        catch (Exception ex) when (state.BodyReadDeadline?.IsCancellationRequested == true && !message.CancellationSource.IsCancellationRequested)
        {
            // The body-read deadline expired: the source stopped sending mid-body. As before streaming, a response
            // that does not arrive within HttpClient.Timeout is a page failure -- published, not retried.
            var timeoutException = new TimeoutException(
                $"Reading the response body for page items {pageRequest.Describe()} did not complete within the HTTP client timeout of {edFiApiClient.HttpClient.Timeout.TotalSeconds:N0} seconds.",
                ex);

            pageLogger.Error(ex, "{ResourceUrl}: {TimeoutMessage}", message.ResourceUrl, timeoutException.Message);

            var error = new ErrorItemMessage
            {
                IsSourceReadError = true,
                Method = HttpMethod.Get.ToString(),
                ResourceUrl = $"{edFiApiClient.DataManagementApiSegment}{message.ResourceUrl}",
                Exception = timeoutException,
            };

            // Publish the failure
            await errorHandlingBlock.SendErrorAsync(error, message.CancellationSource.Token).ConfigureAwait(false);

            return none;
        }
        catch (Exception ex) when (message.CancellationSource.IsCancellationRequested)
        {
            // Graceful cancellation of the resource's processing (normal flow) -- abandon the page fetch
            // without publishing an error. Besides OperationCanceledException from the request or retry
            // backoff, cancellation aborts an in-progress body parse by disposing the response stream,
            // which surfaces as an ObjectDisposedException/IOException from the reader (or an
            // ArgumentException from StreamReader when the token was already cancelled at registration).
            pageLogger.Debug(
                ex,
                "{MessageResourceUrl}: Cancellation requested while retrieving page of source items starting at {PageStart:l}.",
                message.ResourceUrl, pageRequest.DescribeStart());

            return none;
        }
        catch (Exception ex) when (EdFiApiAuthenticationException.IsRepresentedBy(ex))
        {
            // The source API client can no longer authenticate, so nothing that follows can succeed. Faulting the
            // block reports one authoritative failure instead of an per-page error for every resource still
            // streaming, each of which would rediscover the same dead token.
            throw;
        }
        catch (Exception ex)
        {
            pageLogger.Error(ex, "{ResourceUrl}: {Ex}", message.ResourceUrl, ex);

            // An error occurred while parsing the JSON
            var error = new ErrorItemMessage
            {
                IsSourceReadError = true,
                Method = HttpMethod.Get.ToString(),
                ResourceUrl = $"{edFiApiClient.DataManagementApiSegment}{message.ResourceUrl}",
                Exception = ex,
            };

            // Publish the failure
            await errorHandlingBlock.SendErrorAsync(error, message.CancellationSource.Token).ConfigureAwait(false);

            return none;
        }
    }

    /// <summary>Per-message read context shared by the page iterations (mutable: the enriched logger and the body-read deadline).</summary>
    private sealed class PageReadState<TProcessDataMessage>
    {
        public PageReadState(
            StreamResourcePageMessage<TProcessDataMessage> message,
            Options options,
            ITargetBlock<ErrorItemMessage> errorHandlingBlock,
            IPageRequestSequence pageRequest,
            EdFiApiClient edFiApiClient,
            ILogger pageLogger,
            string changeWindowQueryStringParameters)
        {
            Message = message;
            Options = options;
            ErrorHandlingBlock = errorHandlingBlock;
            PageRequest = pageRequest;
            EdFiApiClient = edFiApiClient;
            PageLogger = pageLogger;
            ChangeWindowQueryStringParameters = changeWindowQueryStringParameters;
        }

        public StreamResourcePageMessage<TProcessDataMessage> Message { get; }

        public Options Options { get; }

        public ITargetBlock<ErrorItemMessage> ErrorHandlingBlock { get; }

        public IPageRequestSequence PageRequest { get; }

        public EdFiApiClient EdFiApiClient { get; }

        public string ChangeWindowQueryStringParameters { get; }

        public ILogger PageLogger { get; set; }

        public CancellationTokenSource BodyReadDeadline { get; set; }
    }
}
