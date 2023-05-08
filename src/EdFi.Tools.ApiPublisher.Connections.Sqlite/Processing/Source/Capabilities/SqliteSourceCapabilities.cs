// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Capabilities;

namespace EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Source.Capabilities;

public class SqliteSourceCapabilities : ISourceCapabilities
{
    public Task<bool> SupportsKeyChangesAsync(string probeResourceKey) => Task.FromResult(true);

    public Task<bool> SupportsDeletesAsync(string probeResourceKey) => Task.FromResult(true);

    public bool SupportsGetItemById
    {
        get => false;
    }
}
