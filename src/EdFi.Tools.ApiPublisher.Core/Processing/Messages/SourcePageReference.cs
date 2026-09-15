// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.Tools.ApiPublisher.Core.Processing.Messages
{
    /// <summary>
    /// Identifies the cursor-paged source page a document was read from, so that a run can record which
    /// pages are safely behind it and resume after the rest (see APIPUB-142). One instance is created per
    /// page and shared by every document of that page.
    /// </summary>
    /// <remarks>
    /// This is identity, not text: <see cref="StreamResourcePageMessage{TProcessDataMessage}.DescribeSourcePage" />
    /// renders the operator-facing locator that goes into published error records, and keeps doing so. The two
    /// are taken at the same moment for the same reason, because the page message is mutated as a partition
    /// walk advances and neither can be derived from it afterwards.
    /// <para>
    /// Only pages read with cursor paging have one. Offset-paged reads, which includes every <c>/deletes</c>
    /// and <c>/keyChanges</c> page, have no partition or page token to record and carry a null reference.
    /// </para>
    /// </remarks>
    /// <param name="ResourceUrl">The source resource URL the page belongs to.</param>
    /// <param name="IsAuthorizationRetryPass">
    /// Which of the two passes over this resource URL read the page. A resource with an authorization retry
    /// pipeline is read once by the pass that reads it first and again by the retry pass, so the passes keep
    /// their progress apart (see APIPUB-133).
    /// </param>
    /// <param name="PartitionIndex">The 1-based partition the page was read from.</param>
    /// <param name="PartitionPageNumber">The 1-based ordinal of the page within that partition.</param>
    /// <param name="PageToken">The page token the page was requested with.</param>
    public sealed record SourcePageReference(
        string ResourceUrl,
        bool IsAuthorizationRetryPass,
        int PartitionIndex,
        int PartitionPageNumber,
        string PageToken);
}
