// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Threading.Tasks.Dataflow;
using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Counting;
using EdFi.Tools.ApiPublisher.Core.Extensions;
using EdFi.Tools.ApiPublisher.Core.Helpers;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using log4net;
using Polly;
using Polly.Contrib.WaitAndRetry;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Counting;

public class EdFiApiSourceTotalCountProvider : ISourceTotalCountProvider
{
    private readonly ISourceEdFiApiClientProvider _sourceEdFiApiClientProvider;
    
    private readonly ILog _logger = LogManager.GetLogger(typeof(EdFiApiSourceTotalCountProvider));

    public EdFiApiSourceTotalCountProvider(ISourceEdFiApiClientProvider sourceEdFiApiClientProvider)
    {
        _sourceEdFiApiClientProvider = sourceEdFiApiClientProvider;
    }
    
    public async Task<(bool, long)> TryGetTotalCountAsync(string resourceUrl, Options options, ChangeWindow? changeWindow, ITargetBlock<ErrorItemMessage> errorHandlingBlock, CancellationToken cancellationToken)
    {
        var edFiApiClient = _sourceEdFiApiClientProvider.GetApiClient();
        
        // Source-specific: Ed-Fi ODS API
        string changeWindowQueryStringParameters = ApiRequestHelper.GetChangeWindowQueryStringParameters(changeWindow);

        var delay = Backoff.ExponentialBackoff(
            TimeSpan.FromMilliseconds(options.RetryStartingDelayMilliseconds),
            options.MaxRetryAttempts);

        int attempt = 0;

        var apiResponse = await Policy
            .HandleResult<HttpResponseMessage>(r => r.StatusCode.IsPotentiallyTransientFailure())
            .WaitAndRetryAsync(delay, (result, ts, retryAttempt, ctx) =>
            {
                _logger.Warn(
                    $"{resourceUrl}: Getting item count from source failed with status '{result.Result.StatusCode}'. Retrying... (retry #{retryAttempt} of {options.MaxRetryAttempts} with {ts.TotalSeconds:N1}s delay)");
            })
            .ExecuteAsync((ctx, ct) =>
            {
                attempt++;
                
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"{resourceUrl}): Getting item count from source (attempt #{attempt})...");
                }

                return edFiApiClient.HttpClient.GetAsync(
                    $"{edFiApiClient.DataManagementApiSegment}{resourceUrl}?offset=0&limit=1&totalCount=true{changeWindowQueryStringParameters}",
                    ct);
            }, new Context(), cancellationToken);

        string responseContent = null;

        if (!apiResponse.IsSuccessStatusCode)
        {
            _logger.Error(
                $"{resourceUrl}: Count request returned {apiResponse.StatusCode}{Environment.NewLine}{responseContent}");

            await HandleResourceCountRequestErrorAsync(resourceUrl, errorHandlingBlock, apiResponse)
                .ConfigureAwait(false);

            // Allow processing to continue with no additional work on this resource
            return (false, 0); // Enumerable.Empty<StreamResourcePageMessage<TItemActionMessage>>();
        }

        // Try to get the count header from the response
        if (!apiResponse.Headers.TryGetValues("total-count", out IEnumerable<string> headerValues))
        {
            _logger.Warn(
                $"{resourceUrl}: Unable to obtain total count because Total-Count header was not returned by the source API -- skipping item processing, but overall processing will fail.");

            // Publish an error for the resource. Feature is not supported.
            await HandleResourceCountRequestErrorAsync(resourceUrl, errorHandlingBlock, apiResponse)
                .ConfigureAwait(false);

            // Allow processing to continue as best it can with no additional work on this resource
            return (false, 0); // Enumerable.Empty<StreamResourcePageMessage<TItemActionMessage>>();
        }

        string totalCountHeaderValue = headerValues.First();

        _logger.Debug($"{resourceUrl}: Total count header value = {totalCountHeaderValue}");

        try
        {
            long totalCount = long.Parse(totalCountHeaderValue);

            return (true, totalCount);
        }
        catch (Exception)
        {
            // Publish an error for the resource to allow processing to continue, but to force failure.
            _logger.Error(
                $"{resourceUrl}: Unable to convert Total-Count header value of '{totalCountHeaderValue}'  returned by the source API to an integer.");

            errorHandlingBlock.Post(
                new ErrorItemMessage
                {
                    ResourceUrl = $"{edFiApiClient.DataManagementApiSegment}{resourceUrl}",
                    Method = HttpMethod.Get.ToString(),
                    ResponseStatus = apiResponse.StatusCode,
                    ResponseContent = $"Total-Count: {totalCountHeaderValue}",
                });

            // Allow processing to continue without performing additional work on this resource.
            return (false, 0);
        }
    }

    private async Task HandleResourceCountRequestErrorAsync(
        string resourceUrl,
        ITargetBlock<ErrorItemMessage> errorHandlingBlock,
        HttpResponseMessage apiResponse)
    {
        string responseContent = await apiResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

        // Was this an authorization failure?
        if (apiResponse.StatusCode == HttpStatusCode.Forbidden)
        {
            // Is this a descriptor resource?
            if (ResourcePathHelper.IsDescriptor(resourceUrl))
            {
                // Being denied read access to descriptors is potentially problematic, but is not considered
                // to be breaking in its own right for change processing. We'll fail downstream
                // POSTs if descriptors haven't been initialized correctly on the target.
                _logger.Warn(
                    $"{resourceUrl}: {apiResponse.StatusCode} - Unable to obtain total count for descriptor due to authorization failure. Descriptor values will not be published to the target, but processing will continue.{Environment.NewLine}Response content: {responseContent}");

                return;
            }
        }

        _logger.Error(
            $"{resourceUrl}: {apiResponse.StatusCode} - Unable to obtain total count due to request failure. This resource will not be processed. Downstream failures are possible.{Environment.NewLine}Response content: {responseContent}");

        // Publish an error for the resource to allow processing to continue, but to force failure.
        errorHandlingBlock.Post(
            new ErrorItemMessage
            {
                ResourceUrl = $"{_sourceEdFiApiClientProvider.GetApiClient().DataManagementApiSegment}{resourceUrl}",
                Method = HttpMethod.Get.ToString(),
                ResponseStatus = apiResponse.StatusCode,
                ResponseContent = await apiResponse.Content.ReadAsStringAsync().ConfigureAwait(false),
            });
    }
}
