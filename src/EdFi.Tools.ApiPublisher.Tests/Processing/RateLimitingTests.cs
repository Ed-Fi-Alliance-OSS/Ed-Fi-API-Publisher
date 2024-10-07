// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Autofac.Core;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Shouldly;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    [TestFixture]
    public class RateLimitingTests
    {
        [TestFixture]
        public class When_rate_limiting_is_enabled : TestFixtureAsyncBase
        {
            [Test]
            public async Task RateLimitedMethod_Should_Handle_Parallel_Requests()
            {
                // Rate Limit of 5 executions during 1 second
                var options = TestHelpers.GetOptions();
                options.EnableRateLimit = true;
                options.RateLimitNumberExecutions = 5;
                options.RateLimitTimeSeconds = 1;
                var rateLimiter = new PollyRateLimiter<HttpResponseMessage>(options);

                var methodToTest = new MockRateLimitingMethod(rateLimiter);
                var tasks = new List<Task<HttpResponseMessage>>();
                for (int i = 0; i < 5; i++)
                {
                    tasks.Add(methodToTest.ExecuteAsync(i));
                }
                var result = await Task.WhenAll(tasks);
                result.Should().HaveCount(5);
                result.All(x => x.Content.ReadAsStringAsync().Result == "Execution completed successfully!").ShouldBeTrue();
            }

            [Test]
            public void RateLimitedMethod_Should_Throw_RateLimiterRejectedException_On_Overload()
            {
                var options = TestHelpers.GetOptions();
                options.EnableRateLimit = true;
                options.RateLimitNumberExecutions = 5;
                options.RateLimitTimeSeconds = 1;
                options.RateLimitMaxRetries = 1;
                var rateLimiter = new PollyRateLimiter<HttpResponseMessage>(options);

                var methodToTest = new MockRateLimitingMethod(rateLimiter);

                var tasks = new List<Task<HttpResponseMessage>>();
                // Act
                for (int i = 0; i < 10; i++)
                {
                    tasks.Add(methodToTest.ExecuteAsync());
                }
                Assert.ThrowsAsync<Polly.RateLimit.RateLimitRejectedException>(async () => await Task.WhenAll(tasks));
            }
        }
    }
}
