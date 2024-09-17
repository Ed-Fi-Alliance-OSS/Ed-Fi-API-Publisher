// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.
using Polly.RateLimit;
using Polly;
using System;
using System.Threading.Tasks;
using System.Net.Http;
using System.Threading.RateLimiting;
using Serilog;

namespace EdFi.Tools.ApiPublisher.Core.Configuration;

public class PollyRateLimiter<TResult> : IRateLimiting<TResult>
{
    private readonly IAsyncPolicy<TResult> _rateLimiter;
    private readonly IAsyncPolicy<TResult> _retryPolicyForRateLimit;
    private readonly ILogger _logger = Log.ForContext(typeof(PollyRateLimiter<TResult>));

    public PollyRateLimiter(Options options)
    {
        _rateLimiter = Policy.RateLimitAsync<TResult>(
            options.RateLimitNumberExecutions, 
            TimeSpan.FromSeconds(options.RateLimitTimeSeconds),
            options.RateLimitNumberExecutions);
        _retryPolicyForRateLimit = Policy<TResult>
            .Handle<RateLimitRejectedException>()
            .WaitAndRetryAsync(options.RateLimitMaxRetries,  // Number of retries
                retryAttempt => TimeSpan.FromSeconds(options.RateLimitTimeSeconds),
                (exception, timeSpan, retryCount, context) =>
                {
                    var delay = TimeSpan.FromSeconds(options.RateLimitTimeSeconds);
                    _logger.Warning($"Retry {retryCount} due to rate limit exceeded. Waiting {delay.TotalSeconds} seconds before next retry.");
                }
            );
    }
        
    public async Task<TResult> ExecuteAsync(Func<Task<TResult>> action)
    {
        try
        {
            return await _rateLimiter.ExecuteAsync(action);
        }
        catch (RateLimitRejectedException) {
            _logger.Fatal("Rate limit exceeded. Please try again later.");
            throw;
        }

    }

    public IAsyncPolicy<TResult> GetRateLimitingPolicy()
    {
        return Policy.WrapAsync(_retryPolicyForRateLimit, _rateLimiter);
    }
}
