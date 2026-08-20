// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement
{
    /// <summary>
    /// Decides when the bearer token is refreshed, and how long a failing refresh keeps being retried before
    /// publishing has to stop. A failed refresh does not affect publishing while the current token is still valid,
    /// so the remaining lifetime of that token is what determines how much time there is left to recover.
    /// </summary>
    public static class BearerTokenRefreshPolicy
    {
        /// <summary>
        /// How many consecutive failures are tolerated when the API does not report the lifetime of the token it
        /// issues, which leaves no runway to measure and the failure count as the only thing to go on.
        /// </summary>
        public const int MaxConsecutiveFailuresWithUnknownLifetime = 5;

        /// <summary>
        /// The refresh interval is never shortened below this, so that a very short reported lifetime cannot turn
        /// into a stream of token requests.
        /// </summary>
        public static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromSeconds(30);

        /// <summary>
        /// How much of the token's lifetime is reserved for the requests already in flight. Once less than this is
        /// left, retrying the refresh is pointless.
        /// </summary>
        public static readonly TimeSpan ExpirationMargin = TimeSpan.FromSeconds(30);

        private static readonly TimeSpan _initialRetryDelay = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan _maxRetryDelay = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Gets the interval at which the token is refreshed. When the API reports the lifetime of the token, the
        /// interval is capped at half of it, which leaves room for more than one attempt per token.
        /// </summary>
        public static TimeSpan GetRefreshInterval(TimeSpan configuredInterval, TimeSpan? tokenLifetime)
        {
            if (tokenLifetime == null || tokenLifetime <= TimeSpan.Zero)
            {
                return configuredInterval;
            }

            var halfOfTokenLifetime = TimeSpan.FromTicks(tokenLifetime.Value.Ticks / 2);

            if (halfOfTokenLifetime >= configuredInterval)
            {
                return configuredInterval;
            }

            return halfOfTokenLifetime > MinimumRefreshInterval ? halfOfTokenLifetime : MinimumRefreshInterval;
        }

        /// <summary>
        /// Determines whether a failed refresh should be retried, and after how long.
        /// </summary>
        /// <param name="consecutiveFailures">The number of consecutive failed acquisitions, including this one.</param>
        /// <param name="remainingTokenLifetime">What is left of the current token's lifetime, or <b>null</b> when the
        /// API does not report a lifetime or the token has already been rejected.</param>
        /// <param name="retryDelay">How long to wait before the next attempt.</param>
        /// <returns><b>false</b> when there is no useful time left to retry, which means publishing has to stop.</returns>
        public static bool TryGetRetryDelay(
            int consecutiveFailures,
            TimeSpan? remainingTokenLifetime,
            out TimeSpan retryDelay
        )
        {
            retryDelay = GetBackoffDelay(consecutiveFailures);

            if (remainingTokenLifetime == null)
            {
                return consecutiveFailures < MaxConsecutiveFailuresWithUnknownLifetime;
            }

            var usableLifetime = remainingTokenLifetime.Value - ExpirationMargin;

            if (usableLifetime <= TimeSpan.Zero)
            {
                return false;
            }

            // Never schedule the next attempt past the point where the current token is still usable.
            if (retryDelay > usableLifetime)
            {
                retryDelay = usableLifetime;
            }

            return true;
        }

        private static TimeSpan GetBackoffDelay(int consecutiveFailures)
        {
            double seconds =
                _initialRetryDelay.TotalSeconds * Math.Pow(2, Math.Max(consecutiveFailures - 1, 0));

            return seconds >= _maxRetryDelay.TotalSeconds ? _maxRetryDelay : TimeSpan.FromSeconds(seconds);
        }
    }
}
