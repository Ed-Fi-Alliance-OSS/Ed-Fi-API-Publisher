// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Connections.Api.Helpers;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Paging;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Counting;
using EdFi.Tools.ApiPublisher.Core.Extensions;
using EdFi.Tools.ApiPublisher.Core.Helpers;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using Newtonsoft.Json.Linq;
using Polly;
using Polly.Contrib.WaitAndRetry;
using Polly.RateLimit;
using Serilog;
using Serilog.Events;
using System.Net;
using System.Threading.Tasks.Dataflow;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.MessageProducers;

/// <summary>
/// Produces one page message per partition of a resource using the ODS/API 7.3+ <c>/partitions</c> endpoint
/// (see APIPUB-139). Each message carries a starting <c>pageToken</c>; the read handler walks the partition by
/// following <c>Next-Page-Token</c>. The total count is still requested (offset syntax, concurrently with the
/// partitions request) for the progress log line.
/// </summary>
public class EdFiApiCursorPagingStreamResourcePageMessageProducer
{
    /// <summary>Maximum number of characters of a failed /partitions response body written to the log.</summary>
    public const int MaxLoggedErrorBodyLength = 1_000;

    private readonly ISourceEdFiApiClientProvider _sourceEdFiApiClientProvider;
    private readonly ISourceTotalCountProvider _sourceTotalCountProvider;
    private readonly IRateLimiting<HttpResponseMessage> _rateLimiter;

    private readonly ILogger _logger = Log.ForContext(typeof(EdFiApiCursorPagingStreamResourcePageMessageProducer));

    public EdFiApiCursorPagingStreamResourcePageMessageProducer(
        ISourceEdFiApiClientProvider sourceEdFiApiClientProvider,
        ISourceTotalCountProvider sourceTotalCountProvider,
        IRateLimiting<HttpResponseMessage> rateLimiter = null)
    {
        _sourceEdFiApiClientProvider = sourceEdFiApiClientProvider
            ?? throw new ArgumentNullException(nameof(sourceEdFiApiClientProvider));

        _sourceTotalCountProvider = sourceTotalCountProvider
            ?? throw new ArgumentNullException(nameof(sourceTotalCountProvider));

        _rateLimiter = rateLimiter;
    }

    /// <summary>
    /// Produces the partition page messages for the resource. Returns <c>Success == false</c> (and no messages)
    /// when the partitions request failed, so the caller can fall back to offset paging for this resource.
    /// </summary>
    public async Task<(bool Success, IEnumerable<StreamResourcePageMessage<TProcessDataMessage>> Messages)> TryProduceMessagesAsync<TProcessDataMessage>(
        StreamResourceMessage message,
        Options options,
        ITargetBlock<ErrorItemMessage> errorHandlingBlock,
        Func<StreamResourcePageMessage<TProcessDataMessage>, TextReader, Action<int>, IEnumerable<TProcessDataMessage>> createProcessDataMessages,
        CancellationToken cancellationToken)
    {
        int partitionCount = options.ResolvedCursorPagingPartitionCount;

        // The total count is informational under cursor paging (totalCount is not supported with pageToken); it is
        // still requested on offset syntax so the "Total count = N" line and its consumers behave as before. The count
        // and the partitions request are independent, so the count goes out first and the partitions request follows
        // while it is still in flight -- issued in series, the pre-page phase cost a full round trip more than offset
        // paging on every resource.
        var totalCountTask = GetTotalCountAsync(message, options, errorHandlingBlock, cancellationToken);

        _logger.Information("{ResourceUrl}: Retrieving up to {PartitionCount} partition tokens.", message.ResourceUrl, partitionCount);

        string[] pageTokens = null;
        bool partitionsRequestFailed = false;

        try
        {
            pageTokens = await GetPageTokensAsync(message.ResourceUrl, message.ChangeWindow, partitionCount, options, cancellationToken)
                .ConfigureAwait(false);
        }
        // Run cancellation, the rate limiter and an authentication failure end the run as they would anywhere else.
        // A cancellation the run did not ask for is HttpClient.Timeout expiring while waiting for the headers (it
        // surfaces as a TaskCanceledException), which is a failed partitions request like any other and falls back.
        catch (Exception ex) when ((ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                                   && ex is not RateLimitRejectedException
                                   && !EdFiApiAuthenticationException.IsRepresentedBy(ex))
        {
            _logger.Warning(ex, "{ResourceUrl}: Partitions request failed. Falling back to offset/limit paging for this resource.", message.ResourceUrl);

            partitionsRequestFailed = true;
        }

        // Always awaited (also on the fallback path) so the count request is never left as an orphaned task; a
        // fatal failure it raises (authentication, cancellation) surfaces here exactly as it would have in series
        var (totalCountSuccess, totalCount) = await totalCountTask.ConfigureAwait(false);

        if (partitionsRequestFailed || pageTokens is null)
        {
            // Non-success or failed partitions request, already logged; the offset producer requests its own count
            return (false, null);
        }

        if (!totalCountSuccess)
        {
            // Mirrors the offset producer: the count provider has published the error; do no further work on the resource
            return (true, Enumerable.Empty<StreamResourcePageMessage<TProcessDataMessage>>());
        }

        _logger.Information("{ResourceUrl}: Total count = {TotalCount}", message.ResourceUrl, totalCount);

        if (pageTokens.Length == 0)
        {
            // An empty change window returns {"pageTokens": []} -- nothing to read, not an error (see APIPUB-136)
            _logger.Information("{ResourceUrl}: Source returned no partitions (no items to read).", message.ResourceUrl);

            return (true, Enumerable.Empty<StreamResourcePageMessage<TProcessDataMessage>>());
        }

        if (_logger.IsEnabled(LogEventLevel.Debug))
        {
            _logger.Debug("{ResourceUrl}: Source returned {PartitionsReturned} partition(s) for {PartitionsRequested} requested.",
                message.ResourceUrl, pageTokens.Length, partitionCount);
        }

        var pageMessages = pageTokens
            .Select((pageToken, index) => new StreamResourcePageMessage<TProcessDataMessage>
            {
                // Resource-specific context
                ResourceUrl = message.ResourceUrl,
                HasAuthorizationRetryPipeline = message.HasAuthorizationRetryPipeline,

                // Page-strategy specific context (cursor)
                PageToken = pageToken,
                PageSize = message.PageSize,
                PartitionIndex = index + 1,

                // Global processing context
                ChangeWindow = message.ChangeWindow,
                CreateProcessDataMessages = createProcessDataMessages,
                CancellationSource = message.CancellationSource,
            })
            .ToList();

        return (true, pageMessages);
    }

    /// <summary>
    /// Requests the total count on offset syntax (the same request the offset producer makes), logging the same
    /// "Retrieving total count" line. The provider reports non-fatal failures through its result and the error block.
    /// </summary>
    private Task<(bool Success, long TotalCount)> GetTotalCountAsync(
        StreamResourceMessage message,
        Options options,
        ITargetBlock<ErrorItemMessage> errorHandlingBlock,
        CancellationToken cancellationToken)
    {
        if (message.ChangeWindow?.MaxChangeVersion != default(long) && message.ChangeWindow?.MaxChangeVersion != null)
        {
            _logger.Information("{ResourceUrl}: Retrieving total count of items in change versions {MinChangeVersion} to {MaxChangeVersion}.",
                message.ResourceUrl, message.ChangeWindow.MinChangeVersion, message.ChangeWindow.MaxChangeVersion);
        }
        else
        {
            _logger.Information("{ResourceUrl}: Retrieving total count of items.", message.ResourceUrl);
        }

        return _sourceTotalCountProvider.TryGetTotalCountAsync(
            message.ResourceUrl, options, message.ChangeWindow, errorHandlingBlock, cancellationToken);
    }

    /// <summary>
    /// Requests the partition tokens, retrying transient failures like the count provider does.
    /// Returns <b>null</b> for a non-success response (logged as a Warning) or a body without a pageTokens array.
    /// </summary>
    private async Task<string[]> GetPageTokensAsync(
        string resourceUrl,
        ChangeWindow changeWindow,
        int partitionCount,
        Options options,
        CancellationToken cancellationToken)
    {
        var edFiApiClient = _sourceEdFiApiClientProvider.GetApiClient();

        string changeWindowQueryStringParameters = ApiRequestHelper.GetChangeWindowQueryStringParameters(changeWindow);

        string requestUri =
            $"{edFiApiClient.DataManagementApiSegment}{resourceUrl}{EdFiApiConstants.PartitionsPathSuffix}?number={partitionCount}{changeWindowQueryStringParameters}";

        var delay = Backoff.ExponentialBackoff(
            TimeSpan.FromMilliseconds(options.RetryStartingDelayMilliseconds),
            options.MaxRetryAttempts);

        int attempt = 0;

        var retryPolicy = Policy
            .HandleResult<HttpResponseMessage>(r => r.StatusCode.IsPotentiallyTransientFailure())
            .WaitAndRetryAsync(
                delay,
                (result, ts, retryAttempt, ctx) =>
                {
                    _logger.Warning("{ResourceUrl}: Getting partition tokens from source failed with status '{StatusCode}'. Retrying... (retry #{RetryAttempt} of {MaxRetryAttempts} with {TotalSeconds:N1}s delay)",
                        resourceUrl, result.Result.StatusCode, retryAttempt, options.MaxRetryAttempts, ts.TotalSeconds);

                    // Release the transient failure being retried (see APIPUB-134)
                    result.Result?.Dispose();
                });

        IAsyncPolicy<HttpResponseMessage> policy = options.EnableRateLimit
            ? Policy.WrapAsync(_rateLimiter?.GetRateLimitingPolicy(), retryPolicy)
            : retryPolicy;

        using var apiResponse = await policy.ExecuteAsync(
            (ctx, ct) =>
            {
                attempt++;

                if (_logger.IsEnabled(LogEventLevel.Debug))
                {
                    _logger.Debug("{ResourceUrl}: Getting partition tokens from source (attempt #{Attempt})...", resourceUrl, attempt);
                }

                return RequestHelpers.SendGetRequestAsync(edFiApiClient, resourceUrl, requestUri, ct);
            },
            new Context(),
            cancellationToken).ConfigureAwait(false);

        // Partition bodies are small, a list of tokens, so buffering as a string is deliberate, under the size cap the
        // reader enforces (an oversize body is a failed partitions request). Because requests use ResponseHeadersRead
        // (see APIPUB-134), HttpClient.Timeout covers only the wait for the headers. The body read therefore gets its
        // own deadline of the same length, mirroring the count provider, instead of waiting indefinitely. A stalled body
        // is a failed partitions request too, and both fall back to offset/limit paging like any other failure.
        string content;

        using (var bodyReadDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            bodyReadDeadline.CancelAfter(edFiApiClient.HttpClient.Timeout);

            try
            {
                content = await PartitionsResponseBody.ReadAsync(apiResponse.Content, bodyReadDeadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Reading the partitions response body did not complete within the HTTP client timeout of {edFiApiClient.HttpClient.Timeout.TotalSeconds:N0} seconds.",
                    ex);
            }
        }

        // Only HTTP 200 is the documented partitions response (any other 2xx is not it either). The body is logged
        // to help diagnose the fallback but is capped: it is not under our control and may be large or sensitive.
        if (apiResponse.StatusCode != HttpStatusCode.OK)
        {
            _logger.Warning("{ResourceUrl}: Partitions request returned status '{StatusCode}'. Falling back to offset/limit paging for this resource. Response: {Content}",
                resourceUrl, apiResponse.StatusCode, TruncateForLog(content));

            return null;
        }

        if (JObject.Parse(content)["pageTokens"] is not JArray tokens)
        {
            _logger.Warning("{ResourceUrl}: Partitions response did not contain a 'pageTokens' array. Falling back to offset/limit paging for this resource.", resourceUrl);

            return null;
        }

        // Every element must be a non-empty string: dropping a null or empty entry would silently skip a partition
        // (and a lone null would read as an empty resource), so a malformed array is a failed partitions request
        if (tokens.Any(t => t.Type != JTokenType.String || string.IsNullOrEmpty(t.Value<string>())))
        {
            _logger.Warning("{ResourceUrl}: Partitions response contained a null or empty page token. Falling back to offset/limit paging for this resource.", resourceUrl);

            return null;
        }

        // The 1..200 validation caps what is requested, not what is accepted: a source returning more tokens than
        // asked for would spawn that many partition walks, so it is treated as a failed partitions request. Fewer is
        // fine -- a small resource legitimately yields fewer partitions than requested.
        if (tokens.Count > partitionCount)
        {
            _logger.Warning("{ResourceUrl}: Partitions response returned {PartitionsReturned} page tokens for {PartitionsRequested} requested. Falling back to offset/limit paging for this resource.",
                resourceUrl, tokens.Count, partitionCount);

            return null;
        }

        return tokens.Select(t => t.Value<string>()).ToArray();
    }

    private static string TruncateForLog(string content)
        => content.Length <= MaxLoggedErrorBodyLength
            ? content
            : $"{content[..MaxLoggedErrorBodyLength]}... (truncated, {content.Length:N0} characters)";
}
