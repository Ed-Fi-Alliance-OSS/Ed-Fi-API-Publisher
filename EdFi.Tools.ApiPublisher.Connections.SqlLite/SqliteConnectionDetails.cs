// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;

namespace EdFi.Tools.ApiPublisher.Connections.SqlLite;

public class SqliteConnectionDetails : ITargetConnectionDetails
{
    public string Name { get; set; }

    public string Url { get; set; }
    
    public bool IsFullyDefined() => !string.IsNullOrEmpty(Url);

    /// <summary>
    /// Indicates that the Sqlite connection information does not need additional resolution.
    /// </summary>
    /// <returns>Always returns <b>false</b>.</returns>
    public bool NeedsResolution() => false;
}
