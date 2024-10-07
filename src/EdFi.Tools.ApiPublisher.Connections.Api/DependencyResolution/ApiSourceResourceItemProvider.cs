// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Extensions;
using Polly;
using Polly.Contrib.WaitAndRetry;
using Polly.RateLimit;
using Polly.RateLimiting;
using Serilog;
using Serilog.Events;
using System.Net;

namespace EdFi.Tools.ApiPublisher.Connections.Api.DependencyResolution;

public class ApiSourceResourceItemProvider : ISourceResourceItemProvider
{
    private readonly ISourceEdFiApiClientProvider _sourceEdFiApiClientProvider;
    private readonly IRateLimiting<HttpResponseMessage> _rateLimiter;
    private readonly Options _options;

    private readonly ILogger _logger = Log.ForContext(typeof(ApiSourceResourceItemProvider));

    public ApiSourceResourceItemProvider(ISourceEdFiApiClientProvider sourceEdFiApiClientProvider, Options options, IRateLimiting<HttpResponseMessage> rateLimiter = null)
    {
        _sourceEdFiApiClientProvider = sourceEdFiApiClientProvider;
        _options = options;
        _rateLimiter = rateLimiter;
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
        // Rate Limit
        bool isRateLimitingEnabled = _options.EnableRateLimit;
        var retryPolicy = Policy
            .HandleResult<HttpResponseMessage>(r => r.StatusCode.IsPotentiallyTransientFailure())
            .WaitAndRetryAsync(
                getByIdDelay,
                (result, ts, retryAttempt, ctx) =>
                {
                    _logger.Warning("Retrying GET for resource item '{ResourceItemUrl}' from source failed with status '{StatusCode}'. Retrying... (retry #{RetryAttempt} of {MaxRetryAttempts} with {TotalSeconds:N1}s delay)",
                        resourceItemUrl, result.Result.StatusCode, retryAttempt, _options.MaxRetryAttempts, ts.TotalSeconds);
                });
        IAsyncPolicy<HttpResponseMessage> policy = isRateLimitingEnabled ? Policy.WrapAsync(_rateLimiter?.GetRateLimitingPolicy(), retryPolicy) : retryPolicy;
        try
        {
            var getByIdResponse = await policy.ExecuteAsync(
                    (ctx, ct) =>
                    {
                        getByIdAttempts++;

                        if (getByIdAttempts > 1 && _logger.IsEnabled(LogEventLevel.Debug))
                        {
                            _logger.Debug("GET for missing dependency '{ResourceItemUrl}' reference from source attempt #{GetByIdAttempts}.",
                                resourceItemUrl, getByIdAttempts);
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
            }
            else
            {
                _logger.Warning("GET request from source API for '{ResourceItemUrl}' reference failed with status '{StatusCode}': {ResponseContent}",
                    resourceItemUrl, getByIdResponse.StatusCode, responseContent);

                return (false, null);
            }
        }
        catch (RateLimitRejectedException ex)
        {
            _logger.Fatal(ex, "{DataManagementApiSegment}{ResourceItemUrl}: Rate limit exceeded. Please try again later.",
                sourceEdFiApiClient.DataManagementApiSegment, resourceItemUrl);
            return (false, null);
        }
        //----------------------------------------------------------------------------------------------
    }
}
