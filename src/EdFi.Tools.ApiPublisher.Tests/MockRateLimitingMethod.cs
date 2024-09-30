// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using Polly.RateLimit;
using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Tests
{
    public class MockRateLimitingMethod
    {
        private readonly IRateLimiting<HttpResponseMessage> _rateLimiter;

        public MockRateLimitingMethod(IRateLimiting<HttpResponseMessage> rateLimiter)
        {
            _rateLimiter = rateLimiter;
        }

        public async Task<HttpResponseMessage> ExecuteAsync(int id = 0)
        {
            return await _rateLimiter.ExecuteAsync(async () =>
                {
                    await Task.Delay(100);
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent("Execution completed successfully!")
                    };
                });
        }

    }
}
