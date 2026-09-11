// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Paging;

/// <summary>
/// How pages of a source resource are addressed against the Ed-Fi ODS API.
/// </summary>
public enum SourcePagingStrategy
{
    /// <summary>offset/limit query string parameters (all API versions).</summary>
    Offset,

    /// <summary>Partitioned cursor paging: /partitions starting tokens plus pageToken/pageSize (ODS/API 7.3+).</summary>
    Cursor,
}
