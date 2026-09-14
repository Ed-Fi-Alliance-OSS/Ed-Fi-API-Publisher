// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.Tools.ApiPublisher.Core.Processing.RunState;

/// <summary>
/// How far one partition of one resource got (see APIPUB-142).
/// </summary>
public class PublishRunPartitionState
{
    /// <summary>The 1-based partition index, as the source returned it from <c>/partitions</c>.</summary>
    public int PartitionIndex { get; set; }

    /// <summary>
    /// The token the source handed back for this partition. A resume replays it rather than asking for
    /// partitions again, so that it walks the same ranges the original run was given.
    /// </summary>
    public string StartingPageToken { get; set; }

    /// <summary>
    /// The token of the last page whose documents all reached the target, or null when no page of this
    /// partition has got that far. A resume starts the walk at this token, which re-reads that one page and
    /// then carries on: the token names the page to request, and the source answers it with that page.
    /// </summary>
    public string LastCompletedPageToken { get; set; }

    /// <summary>The 1-based ordinal of that page within the partition, for the log line on resume.</summary>
    public int LastCompletedPageNumber { get; set; }
}
