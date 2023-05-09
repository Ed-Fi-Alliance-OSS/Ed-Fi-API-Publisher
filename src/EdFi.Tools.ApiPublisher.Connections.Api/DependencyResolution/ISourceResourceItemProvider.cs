// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.Tools.ApiPublisher.Connections.Api.DependencyResolution;

// NOTE: While currently this is only supported by the API as source, it should probably be treated as a global interface
// (moved to Core project).

public interface ISourceResourceItemProvider
{
    Task<(bool success, string itemJson)> TryGetResourceItemAsync(string resourceItemUrl);
}
