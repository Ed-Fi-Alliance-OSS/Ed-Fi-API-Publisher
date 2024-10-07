// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Threading.Tasks.Dataflow;
using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Connections.Api.Helpers;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Counting;
using EdFi.Tools.ApiPublisher.Core.Extensions;
using EdFi.Tools.ApiPublisher.Core.Helpers;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using Polly;
using Polly.Contrib.WaitAndRetry;
using Polly.RateLimit;
using Serilog;
using Serilog.Events;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Counting;

public class EdFiApiSourceTotalCountProvider : ISourceTotalCountProvider
{
    private readonly ISourceEdFiApiClientProvider _sourceEdFiApiClientProvider;

    private readonly ILogger _logger = Log.ForContext(typeof(EdFiApiSourceTotalCountProvider));

    private readonly IRateLimiting<HttpResponseMessage> _rateLimiter;

    public EdFiApiSourceTotalCountProvider(
        ISourceEdFiApiClientProvider sourceEdFiApiClientProvider,
        IRateLimiting<HttpResponseMessage> rateLimiter = null
    )
    {
        _sourceEdFiApiClientProvider = sourceEdFiApiClientProvider;
        _rateLimiter = rateLimiter;
    }

    public async Task<(bool, long)> TryGetTotalCountAsync(
        string resourceUrl,
        Options options,
        ChangeWindow changeWindow,
        ITargetBlock<ErrorItemMessage> errorHandlingBlock,
        CancellationToken cancellationToken
    )
    {
        var edFiApiClient = _sourceEdFiApiClientProvider.GetApiClient();

        // Source-specific: Ed-Fi ODS API
        string changeWindowQueryStringParameters = ApiRequestHelper.GetChangeWindowQueryStringParameters(
            changeWindow
        );

        var delay = Backoff.ExponentialBackoff(
            TimeSpan.FromMilliseconds(options.RetryStartingDelayMilliseconds),
            options.MaxRetryAttempts
        );

        int attempt = 0;

        // Rate Limit
        bool isRateLimitingEnabled = options.EnableRateLimit;

        var retryPolicy = Policy
            .HandleResult<HttpResponseMessage>(r => r.StatusCode.IsPotentiallyTransientFailure())
            .WaitAndRetryAsync(
                delay,
                (result, ts, retryAttempt, ctx) =>
                {
                    _logger.Warning(
                        "{Url}: Getting item count from source failed with status '{StatusCode}'. Retrying... (retry #{RetryAttempt} of {MaxNumber} with {Seconds:N1}s delay)",
                        resourceUrl,
                        result.Result.StatusCode,
                        retryAttempt,
                        options.MaxRetryAttempts,
                        ts.TotalSeconds
                    );
                }
            );
        IAsyncPolicy<HttpResponseMessage> policy = isRateLimitingEnabled
            ? Policy.WrapAsync(_rateLimiter?.GetRateLimitingPolicy(), retryPolicy)
            : retryPolicy;
        try
        {
            var apiResponse = await policy.ExecuteAsync(
                async (ctx, ct) =>
                {
                    attempt++;

                    if (_logger.IsEnabled(LogEventLevel.Debug))
                    {
                        _logger.Debug(
                            "{Url}): Getting item count from source (attempt #{Attempt})...",
                            resourceUrl,
                            attempt
                        );
                    }

                    string requestUri =
                        $"{edFiApiClient.DataManagementApiSegment}{resourceUrl}?offset=0&limit=1&totalCount=true{changeWindowQueryStringParameters}";

                    return await RequestHelpers.SendGetRequestAsync(
                        edFiApiClient,
                        resourceUrl,
                        requestUri,
                        ct
                    );
                },
                new Context(),
                cancellationToken
            );

            string responseContent = null;

            if (!apiResponse.IsSuccessStatusCode)
            {
                _logger.Error(
                    "{Url}: Count request returned {StatusCode}\r{Content}",
                    resourceUrl,
                    apiResponse.StatusCode,
                    responseContent
                );

                await HandleResourceCountRequestErrorAsync(resourceUrl, errorHandlingBlock, apiResponse)
                    .ConfigureAwait(false);

                // Allow processing to continue with no additional work on this resource
                return (false, 0);
            }

            // Try to get the count header from the response
            if (!apiResponse.Headers.TryGetValues("total-count", out IEnumerable<string> headerValues))
            {
                _logger.Warning(
                    "{Url}: Unable to obtain total count because Total-Count header was not returned by the source API -- skipping item processing, but overall processing will fail.",
                    resourceUrl
                );

                // Publish an error for the resource. Feature is not supported.
                await HandleResourceCountRequestErrorAsync(resourceUrl, errorHandlingBlock, apiResponse)
                    .ConfigureAwait(false);

                // Allow processing to continue as best it can with no additional work on this resource
                return (false, 0);
            }

            string totalCountHeaderValue = headerValues.First();

            _logger.Debug(
                "{Url}: Total count header value = {TotalCount}",
                resourceUrl,
                totalCountHeaderValue
            );

            try
            {
                long totalCount = long.Parse(totalCountHeaderValue);

                return (true, totalCount);
            }
            catch (Exception ex)
            {
                // Publish an error for the resource to allow processing to continue, but to force failure.
                _logger.Error(
                    ex,
                    "{Url}: Unable to convert Total-Count header value of '{TotalCount}'  returned by the source API to an integer.",
                    resourceUrl,
                    totalCountHeaderValue
                );

                errorHandlingBlock.Post(
                    new ErrorItemMessage
                    {
                        ResourceUrl = $"{edFiApiClient.DataManagementApiSegment}{resourceUrl}",
                        Method = HttpMethod.Get.ToString(),
                        ResponseStatus = apiResponse.StatusCode,
                        ResponseContent = $"Total-Count: {totalCountHeaderValue}",
                    }
                );

                // Allow processing to continue without performing additional work on this resource.
                return (false, 0);
            }
        }
        catch (RateLimitRejectedException ex)
        {
            _logger.Fatal(ex, "{Segment}{Url}: Rate limit exceeded. Please try again later.", edFiApiClient.DataManagementApiSegment, resourceUrl);
            return (false, 0);
        }
    }

    private async Task HandleResourceCountRequestErrorAsync(
        string resourceUrl,
        ITargetBlock<ErrorItemMessage> errorHandlingBlock,
        HttpResponseMessage apiResponse
    )
    {
        string responseContent = await apiResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

        // Was this an authorization failure?
        var authFailure = apiResponse.StatusCode == HttpStatusCode.Forbidden;
        var isDescriptor = ResourcePathHelper.IsDescriptor(resourceUrl);
        if (authFailure && isDescriptor)
        {
            // Being denied read access to descriptors is potentially problematic, but is not considered
            // to be breaking in its own right for change processing. We'll fail downstream
            // POSTs if descriptors haven't been initialized correctly on the target.
            _logger.Warning(
                "{Url}: {StatusCode} - Unable to obtain total count for descriptor due to authorization failure. Descriptor values will not be published to the target, but processing will continue.\rResponse content: {Content}",
                resourceUrl, apiResponse.StatusCode, responseContent
            );

            return;
        }

        _logger.Error(
            "{Url}: {StatusCode} - Unable to obtain total count due to request failure. This resource will not be processed. Downstream failures are possible.\rResponse content: {Content}",
            resourceUrl, apiResponse.StatusCode, responseContent
        );

        // Publish an error for the resource to allow processing to continue, but to force failure.
        errorHandlingBlock.Post(
            new ErrorItemMessage
            {
                ResourceUrl =
                    $"{_sourceEdFiApiClientProvider.GetApiClient().DataManagementApiSegment}{resourceUrl}",
                Method = HttpMethod.Get.ToString(),
                ResponseStatus = apiResponse.StatusCode,
                ResponseContent = await apiResponse.Content.ReadAsStringAsync().ConfigureAwait(false),
            }
        );
    }
}
