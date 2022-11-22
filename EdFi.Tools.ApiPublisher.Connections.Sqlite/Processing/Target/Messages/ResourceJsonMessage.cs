// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Target.Messages;

public abstract class ResourceJsonMessage
{
    /// <summary>
    /// Gets or sets the relative URL for the resource associated with the data.
    /// </summary>
    public string ResourceUrl { get; set; }

    /// <summary>
    /// Get or sets the JSON to be stored in the Sqlite database.
    /// </summary>
    public string Json { get; set; }
}

public class KeyChangesJsonMessage : ResourceJsonMessage {}
public class UpsertsJsonMessage : ResourceJsonMessage {}
public class DeletesJsonMessage : ResourceJsonMessage {}