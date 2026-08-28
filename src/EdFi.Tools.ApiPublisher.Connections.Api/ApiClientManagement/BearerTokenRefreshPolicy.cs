// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement
{
    /// <summary>
    /// Decides when the bearer token is refreshed, and how long a failing acquisition keeps being retried before
    /// publishing has to stop. A failed refresh does not affect publishing while the current token is still valid,
    /// so the remaining lifetime of that token is what determines how much time there is left to recover. Once the
    /// token is gone, either expired or rejected, the failure count is all that is left to go on.
    /// </summary>
    public static class BearerTokenRefreshPolicy
    {
        /// <summary>
        /// How many consecutive failures are tolerated once the current token can no longer be relied on: because it
        /// has expired or been rejected, or because the API never reported a lifetime for it.
        /// </summary>
        public const int MaxConsecutiveFailuresWithoutUsableToken = 5;

        /// <summary>
        /// A derived refresh interval below this is reported as a warning, because a token that short-lived costs a
        /// token request every few seconds for the whole run. It is still honored: the refresh has to happen before
        /// the token expires, whatever the lifetime the API chose.
        /// </summary>
        public static readonly TimeSpan ShortRefreshIntervalThreshold = TimeSpan.FromSeconds(30);

        /// <summary>
        /// How much of the token's lifetime is reserved for the requests already in flight. Once less than this is
        /// left, the token is treated as no longer usable.
        /// </summary>
        public static readonly TimeSpan ExpirationMargin = TimeSpan.FromSeconds(30);

        /// <summary>
        /// The delay before the first retry of a failed acquisition. Each further consecutive failure doubles it, up
        /// to <see cref="MaxRetryDelay" />.
        /// </summary>
        public static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(5);

        public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Gets the interval at which the token is refreshed. When the API reports the lifetime of the token, the
        /// interval is capped at half of it, which leaves room for more than one attempt per token and always places
        /// the refresh before the token expires.
        /// </summary>
        public static TimeSpan GetRefreshInterval(TimeSpan configuredInterval, TimeSpan? tokenLifetime)
        {
            if (tokenLifetime is null || tokenLifetime <= TimeSpan.Zero)
            {
                return configuredInterval;
            }

            var halfOfTokenLifetime = TimeSpan.FromTicks(tokenLifetime.Value.Ticks / 2);

            return halfOfTokenLifetime < configuredInterval ? halfOfTokenLifetime : configuredInterval;
        }

        /// <summary>
        /// Determines whether a failed acquisition should be retried, and after how long.
        /// </summary>
        /// <param name="consecutiveFailures">The number of consecutive failed acquisitions, including this one.</param>
        /// <param name="remainingTokenLifetime">What is left of the current token's lifetime, or <b>null</b> when the
        /// API does not report a lifetime or the token has already been rejected.</param>
        /// <param name="retryDelay">How long to wait before the next attempt.</param>
        /// <returns><b>false</b> when there is no point in retrying any further, which means publishing has to stop.</returns>
        public static bool TryGetRetryDelay(
            int consecutiveFailures,
            TimeSpan? remainingTokenLifetime,
            out TimeSpan retryDelay
        )
        {
            retryDelay = GetBackoffDelay(consecutiveFailures);

            var usableLifetime = remainingTokenLifetime - ExpirationMargin;

            if (usableLifetime is null || usableLifetime <= TimeSpan.Zero)
            {
                // There is no usable token to wait out, so the failure count is what decides.
                return consecutiveFailures < MaxConsecutiveFailuresWithoutUsableToken;
            }

            // Never schedule the next attempt past the point where the current token is still usable.
            if (retryDelay > usableLifetime)
            {
                retryDelay = usableLifetime.Value;
            }

            return true;
        }

        private static TimeSpan GetBackoffDelay(int consecutiveFailures)
        {
            double seconds = InitialRetryDelay.TotalSeconds * Math.Pow(2, Math.Max(consecutiveFailures - 1, 0));

            return seconds >= MaxRetryDelay.TotalSeconds ? MaxRetryDelay : TimeSpan.FromSeconds(seconds);
        }
    }
}
