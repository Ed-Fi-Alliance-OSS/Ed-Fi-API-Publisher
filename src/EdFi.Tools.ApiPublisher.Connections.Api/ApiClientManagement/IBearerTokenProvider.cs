// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement
{
    /// <summary>
    /// Supplies the bearer token applied to outgoing requests, and re-acquires it on demand when the API reports
    /// that it is no longer valid.
    /// </summary>
    public interface IBearerTokenProvider
    {
        /// <summary>
        /// Gets the bearer token to apply to outgoing requests, or <b>null</b> if no token has been obtained yet.
        /// </summary>
        string CurrentBearerToken { get; }

        /// <summary>
        /// Gets a value indicating whether authentication has failed to the point that requests can no longer
        /// succeed, and publishing should therefore stop.
        /// </summary>
        bool IsAuthenticationFailed { get; }

        /// <summary>
        /// Re-acquires the bearer token after a request was rejected as unauthorized. Concurrent callers that were
        /// rejected while holding the same stale token result in a single token request.
        /// </summary>
        /// <param name="staleBearerToken">The token that was rejected, used to detect a re-acquisition that another
        /// caller has already performed.</param>
        /// <param name="cancellationToken"></param>
        /// <returns><b>true</b> if a usable token is available; otherwise <b>false</b>.</returns>
        Task<bool> TryReacquireBearerTokenAsync(string staleBearerToken, CancellationToken cancellationToken);
    }
}
