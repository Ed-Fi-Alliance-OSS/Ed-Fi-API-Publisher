// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Blocks;

namespace EdFi.Tools.ApiPublisher.Connections.Api.DependencyResolution;

/// <summary>
/// Implements a resource item provider for use when the source does not have the capability.
/// </summary>
public class FallbackSourceResourceItemProvider : ISourceResourceItemProvider
{
    public Task<(bool success, string itemJson)> TryGetResourceItemAsync(string resourceItemUrl) => Task.FromResult((false, null as string));
}
