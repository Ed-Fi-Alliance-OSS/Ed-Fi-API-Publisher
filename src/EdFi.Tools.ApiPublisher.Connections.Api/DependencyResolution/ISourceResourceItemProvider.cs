// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.Tools.ApiPublisher.Connections.Api.DependencyResolution;

// NOTE: While currently this is only supported by the API as source, it should probably be treated as a global interface
// (moved to Core project).

public interface ISourceResourceItemProvider
{
    /// <summary>
    /// Attempts to retrieve a single resource item from the source by its relative URL.
    /// </summary>
    /// <param name="resourceItemUrl">The relative URL of the source item (e.g. "/ed-fi/students/{id}").</param>
    /// <param name="cancellationToken">Token observed by the request and any retry delays, so that cancelling the
    /// run releases a handler blocked on dependency resolution rather than leaving it to ride out the retries.</param>
    Task<(bool success, string itemJson)> TryGetResourceItemAsync(string resourceItemUrl, CancellationToken cancellationToken = default);
}
