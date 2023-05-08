// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Extensions;
using Serilog;
using Polly;
using Polly.Contrib.WaitAndRetry;
using Serilog.Events;

namespace EdFi.Tools.ApiPublisher.Connections.Api.DependencyResolution;

public class ApiSourceResourceItemProvider : ISourceResourceItemProvider
{
    private readonly ISourceEdFiApiClientProvider _sourceEdFiApiClientProvider;
    private readonly Options _options;
        
    private readonly ILogger _logger = Log.ForContext(typeof(ApiSourceResourceItemProvider));
        
    public ApiSourceResourceItemProvider(ISourceEdFiApiClientProvider sourceEdFiApiClientProvider, Options options)
    {
        _sourceEdFiApiClientProvider = sourceEdFiApiClientProvider;
        _options = options;
    }
        
    public async Task<(bool success, string itemJson)> TryGetResourceItemAsync(string resourceItemUrl)
    {
        var sourceEdFiApiClient = _sourceEdFiApiClientProvider.GetApiClient();
            
        //----------------------------------------------------------------------------------------------
        // Reference resolution
        //----------------------------------------------------------------------------------------------
        var getByIdDelay = Backoff.ExponentialBackoff(
            TimeSpan.FromMilliseconds(_options.RetryStartingDelayMilliseconds),
            _options.MaxRetryAttempts);

        int getByIdAttempts = 0;

        var getByIdResponse = await Policy
            .HandleResult<HttpResponseMessage>(r => r.StatusCode.IsPotentiallyTransientFailure())
            .WaitAndRetryAsync(
                getByIdDelay,
                (result, ts, retryAttempt, ctx) =>
                {
                    _logger.Warning(
                        $"Retrying GET for resource item '{resourceItemUrl}' from source failed with status '{result.Result.StatusCode}'. Retrying... (retry #{retryAttempt} of {_options.MaxRetryAttempts} with {ts.TotalSeconds:N1}s delay)");
                })
            .ExecuteAsync(
                (ctx, ct) =>
                {
                    getByIdAttempts++;

                    if (getByIdAttempts > 1)
                    {
                        if (_logger.IsEnabled(LogEventLevel.Debug))
                        {
                            _logger.Debug(
                                $"GET for missing dependency '{resourceItemUrl}' reference from source attempt #{getByIdAttempts}.");
                        }
                    }

                    return sourceEdFiApiClient.HttpClient.GetAsync(
                        $"{sourceEdFiApiClient.DataManagementApiSegment}{resourceItemUrl}",
                        ct);
                },
                new Context(),
                CancellationToken.None);

        // Detect null content and provide a better error message (which happens only during unit testing if mocked requests aren't properly defined)
        if (getByIdResponse.Content == null)
        {
            throw new NullReferenceException(
                $"Content of response for '{sourceEdFiApiClient.HttpClient.BaseAddress}{sourceEdFiApiClient.DataManagementApiSegment}{resourceItemUrl}' was null.");
        }
        
        string responseContent = await getByIdResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

        // Did we successfully retrieve the missing dependency?
        if (getByIdResponse.StatusCode == HttpStatusCode.OK)
        {
            return (true, responseContent);
            
            // string missingItemJson = await getByIdResponse.Content.ReadAsStringAsync();
            //
            // return missingItemJson;

            /*
            var missingItemDelay = Backoff.ExponentialBackoff(
                TimeSpan.FromMilliseconds(_options.RetryStartingDelayMilliseconds),
                _options.MaxRetryAttempts);

            if (_logger.IsEnabled(LogEventLevel.Debug))
            {
                _logger.Debug(
                    $"{resourceUrl}: Attempting to POST missing '{referencedResourceName}' reference to the target.");
            }

            // Post the resource to target now
            var missingItemPostResponse = await Policy
                .HandleResult<HttpResponseMessage>(r => r.StatusCode.IsPotentiallyTransientFailure())
                .WaitAndRetryAsync(
                    missingItemDelay,
                    (result, ts, retryAttempt, ctx) =>
                    {
                        _logger.Warning(
                            $"{resourceUrl}: Retrying POST for missing '{referencedResourceName}' reference against target failed with status '{result.Result.StatusCode}'. Retrying... (retry #{retryAttempt} of {_options.MaxRetryAttempts} with {ts.TotalSeconds:N1}s delay)");
                    })
                .ExecuteAsync(
                    (ctx, ct) =>
                    {
                        getByIdAttempts++;

                        if (getByIdAttempts > 1)
                        {
                            if (_logger.IsEnabled(LogEventLevel.Debug))
                            {
                                _logger.Debug(
                                    $"{resourceUrl}: GET for missing '{referencedResourceName}' reference from source attempt #{getByIdAttempts}.");
                            }
                        }

                        return targetEdFiApiClient.HttpClient.PostAsync(
                            $"{targetEdFiApiClient.DataManagementApiSegment}{missingDependencyResourcePath}",
                            new StringContent(
                                missingItem.ToString(Formatting.None),
                                Encoding.UTF8,
                                "application/json"),
                            ct);
                    },
                    new Context(),
                    CancellationToken.None);

            if (!missingItemPostResponse.IsSuccessStatusCode)
            {
                string responseContent =
                    await getByIdResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

                _logger.Error(
                    $"{resourceUrl}: POST of missing '{referencedResourceName}' reference to the target returned status '{missingItemPostResponse.StatusCode}': {responseContent}.");
            }
            else
            {
                _logger.Information(
                    $"{resourceUrl}: POST of missing '{referencedResourceName}' reference to the target returned status '{missingItemPostResponse.StatusCode}'.");
            }
            */
        }
        else
        {
            _logger.Warning(
                $"GET request from source API for '{resourceItemUrl}' reference failed with status '{getByIdResponse.StatusCode}': {responseContent}");

            return (false, null);
        }
        //----------------------------------------------------------------------------------------------
    }
}
