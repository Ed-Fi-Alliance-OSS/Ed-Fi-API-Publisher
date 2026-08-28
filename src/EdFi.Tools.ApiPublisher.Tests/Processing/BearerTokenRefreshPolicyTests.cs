// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using NUnit.Framework;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    [TestFixture]
    public class BearerTokenRefreshPolicyTests
    {
        [Test]
        public void When_the_api_reports_no_token_lifetime_the_configured_interval_is_used()
        {
            var interval = BearerTokenRefreshPolicy.GetRefreshInterval(
                TimeSpan.FromMinutes(28),
                tokenLifetime: null);

            Assert.That(interval, Is.EqualTo(TimeSpan.FromMinutes(28)));
        }

        [TestCaseSource(nameof(UnusableTokenLifetimes))]
        public void When_the_reported_token_lifetime_is_not_usable_the_configured_interval_is_used(
            TimeSpan tokenLifetime)
        {
            var interval = BearerTokenRefreshPolicy.GetRefreshInterval(TimeSpan.FromMinutes(28), tokenLifetime);

            Assert.That(interval, Is.EqualTo(TimeSpan.FromMinutes(28)));
        }

        private static readonly TimeSpan[] UnusableTokenLifetimes =
        {
            TimeSpan.Zero,
            TimeSpan.FromSeconds(-1)
        };

        // A 30 minute token, which is what the Ed-Fi ODS / API issues, against the documented default of 28 minutes
        [TestCase(30, 28, 15)]
        // The configured interval is the upper bound, so a long lived token does not stretch it
        [TestCase(120, 28, 28)]
        public void The_refresh_interval_is_capped_at_half_of_the_reported_token_lifetime(
            int tokenLifetimeMinutes,
            int configuredMinutes,
            int expectedMinutes)
        {
            var interval = BearerTokenRefreshPolicy.GetRefreshInterval(
                TimeSpan.FromMinutes(configuredMinutes),
                TimeSpan.FromMinutes(tokenLifetimeMinutes));

            Assert.That(interval, Is.EqualTo(TimeSpan.FromMinutes(expectedMinutes)));
        }

        // Lifetimes short enough that a fixed floor on the interval would place the refresh at or after the expiry
        [TestCase(60, 30)]
        [TestCase(45, 22.5)]
        [TestCase(30, 15)]
        [TestCase(10, 5)]
        public void A_short_token_lifetime_is_still_refreshed_before_the_token_expires(
            double tokenLifetimeSeconds,
            double expectedIntervalSeconds)
        {
            var tokenLifetime = TimeSpan.FromSeconds(tokenLifetimeSeconds);

            var interval = BearerTokenRefreshPolicy.GetRefreshInterval(TimeSpan.FromMinutes(28), tokenLifetime);

            Assert.That(interval, Is.EqualTo(TimeSpan.FromSeconds(expectedIntervalSeconds)));
            Assert.That(interval, Is.LessThan(tokenLifetime), "The refresh must come before the token expires.");
        }

        [TestCase(1, 5)]
        [TestCase(2, 10)]
        [TestCase(3, 20)]
        [TestCase(4, 40)]
        public void With_no_reported_token_lifetime_failures_are_retried_on_an_exponential_backoff(
            int consecutiveFailures,
            int expectedDelaySeconds)
        {
            bool shouldRetry = BearerTokenRefreshPolicy.TryGetRetryDelay(
                consecutiveFailures,
                remainingTokenLifetime: null,
                out var retryDelay);

            Assert.That(shouldRetry, Is.True);
            Assert.That(retryDelay, Is.EqualTo(TimeSpan.FromSeconds(expectedDelaySeconds)));
        }

        [Test]
        public void With_no_reported_token_lifetime_the_failure_count_is_what_ends_the_run()
        {
            bool shouldRetry = BearerTokenRefreshPolicy.TryGetRetryDelay(
                BearerTokenRefreshPolicy.MaxConsecutiveFailuresWithoutUsableToken,
                remainingTokenLifetime: null,
                out _);

            Assert.That(shouldRetry, Is.False);
        }

        [Test]
        public void While_the_current_token_is_still_valid_the_failure_count_does_not_end_the_run()
        {
            // Well past the count that applies when no usable token is left
            const int ConsecutiveFailures = 9;

            bool shouldRetry = BearerTokenRefreshPolicy.TryGetRetryDelay(
                ConsecutiveFailures,
                TimeSpan.FromMinutes(10),
                out var retryDelay);

            Assert.That(shouldRetry, Is.True, "A failed refresh must keep being retried while the token is usable.");
            Assert.That(retryDelay, Is.EqualTo(TimeSpan.FromSeconds(60)), "The backoff is capped at one minute.");
        }

        [Test]
        public void A_retry_is_never_scheduled_past_the_point_where_the_token_is_still_usable()
        {
            var remainingTokenLifetime = BearerTokenRefreshPolicy.ExpirationMargin + TimeSpan.FromSeconds(10);

            // The backoff for a third failure is 20 seconds, which would overshoot the 10 usable seconds left
            bool shouldRetry = BearerTokenRefreshPolicy.TryGetRetryDelay(
                consecutiveFailures: 3,
                remainingTokenLifetime,
                out var retryDelay);

            Assert.That(shouldRetry, Is.True);
            Assert.That(retryDelay, Is.EqualTo(TimeSpan.FromSeconds(10)));
        }

        [TestCaseSource(nameof(LifetimesWithNoUsableTokenLeft))]
        public void Once_no_usable_token_is_left_the_failure_count_is_what_ends_the_run(TimeSpan remainingTokenLifetime)
        {
            // The token is as good as gone, but that alone does not end the run: the request path recovers from a
            // rejected token, so a few more attempts are worth making before giving up.
            Assert.That(
                BearerTokenRefreshPolicy.TryGetRetryDelay(consecutiveFailures: 1, remainingTokenLifetime, out var retryDelay),
                Is.True);
            Assert.That(retryDelay, Is.EqualTo(BearerTokenRefreshPolicy.InitialRetryDelay));

            Assert.That(
                BearerTokenRefreshPolicy.TryGetRetryDelay(
                    BearerTokenRefreshPolicy.MaxConsecutiveFailuresWithoutUsableToken,
                    remainingTokenLifetime,
                    out _),
                Is.False);
        }

        private static readonly TimeSpan[] LifetimesWithNoUsableTokenLeft =
        {
            // About to expire: only the margin reserved for in-flight requests is left
            BearerTokenRefreshPolicy.ExpirationMargin,
            // Already expired
            TimeSpan.FromSeconds(-1)
        };
    }
}
