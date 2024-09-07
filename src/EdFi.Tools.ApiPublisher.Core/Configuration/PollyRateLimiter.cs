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

namespace EdFi.Tools.ApiPublisher.Core.Configuration;

public class PollyRateLimiter<TResult> : IRateLimiting<TResult>
{
    private readonly AsyncRateLimitPolicy<TResult> _rateLimiter;

    public PollyRateLimiter(Options options)
    {
        _rateLimiter = Policy.RateLimitAsync<TResult>(
            options.RateLimitNumberExecutions, 
            TimeSpan.FromSeconds(options.RateLimitTimeSeconds),
            options.RateLimitNumberExecutions);
    }
        
    public async Task<TResult> ExecuteAsync(Func<Task<TResult>> action)
    {
        return await _rateLimiter.ExecuteAsync(action);
    }

    public AsyncRateLimitPolicy<TResult> GetRateLimitingPolicy()
    {
        return _rateLimiter;
    }
}
