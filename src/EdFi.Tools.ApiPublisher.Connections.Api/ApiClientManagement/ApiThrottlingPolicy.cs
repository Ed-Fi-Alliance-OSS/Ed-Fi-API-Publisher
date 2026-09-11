// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement
{
    /// <summary>
    /// How a client paces itself against the API it reads from: how much it will ask of the API at once, and how it
    /// backs off once the API answers that it is being asked for too much.
    /// </summary>
    public class ApiThrottlingPolicy
    {
        /// <summary>
        /// The time one request has to complete, waits for a rejected read included. It is applied as the client's
        /// <see cref="HttpClient.Timeout" />, and the default is the same 100 seconds
        /// <see cref="HttpClient" /> uses when nothing sets it.
        /// </summary>
        public static readonly TimeSpan DefaultRequestBudget = TimeSpan.FromSeconds(100);

        /// <summary>
        /// Asks the API for as much as the caller produces and does not retry a request the API rejects as too many
        /// requests, which is how a client behaves when it has not been configured otherwise.
        /// </summary>
        public static readonly ApiThrottlingPolicy None = new();

        /// <summary>
        /// The most requests the client will have in flight against the API at one time. Zero leaves the API
        /// uncapped.
        /// </summary>
        public int MaxConcurrentRequests { get; init; }

        /// <summary>
        /// The number of times a read the API rejected with 429 Too Many Requests is retried before the rejection is
        /// reported to the caller. Zero leaves the rejection to the caller on the first response.
        /// </summary>
        /// <remarks>
        /// This budget is spent inside one request and is independent of the retry policies that cover other
        /// transient failures, so a read that meets a 429 and then a 503 can spend both.
        /// </remarks>
        public int TooManyRequestsRetryAttempts { get; init; }

        /// <summary>
        /// The first delay of the exponential back off applied between those retries. It is only what the client
        /// falls back on: a 429 that says how long to wait is waited out for at least that long instead.
        /// </summary>
        public TimeSpan TooManyRequestsRetryStartingDelay { get; init; } = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// The time one request has to complete. Every wait a rejected read takes is spent inside it, because the
        /// wait happens within the request rather than around it, so a read is abandoned rather than waited out
        /// once the remaining budget cannot cover the next wait.
        /// </summary>
        public TimeSpan RequestBudget { get; init; } = DefaultRequestBudget;
    }
}
