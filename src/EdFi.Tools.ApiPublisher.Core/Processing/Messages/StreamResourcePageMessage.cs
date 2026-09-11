// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Messages
{
    /// <summary>
    /// Represents details needed for obtaining a page of JSON data from the source connection.
    /// </summary>
    public class StreamResourcePageMessage<TProcessDataMessage>
    {
        // ----------------------------
        // Resource-specific context
        // ----------------------------
        public string ResourceUrl { get; set; }

        // Source Ed-Fi ODS API processing context (resource-specific)
        // Pass-through from the top-level StreamResourceMessage (see its remarks): true when an
        // authorization-retry ("#Retry") pipeline will re-publish this resource after prerequisites complete.
        public bool HasAuthorizationRetryPipeline { get; set; }

        // Indicates that the page belongs to the authorization retry pass, whose documents were already
        // counted as attempted by the first pass (see APIPUB-120).
        public bool IsAuthorizationRetryPass { get; set; }

        // -------------------------------
        // Paging-strategy specific context
        // --------------------------------
        public long? Offset { get; set; }
        public int? Limit { get; set; }
        public string PartitionFrom { get; set; }
        public string PartitionUntil { get; set; }
        public bool IsFinalPage { get; set; }

        // Cursor paging (ODS/API 7.3+, see APIPUB-139): the opaque starting token of the partition this message
        // walks, the page size sent with it, and the 1-based partition index (for logging only)
        public string PageToken { get; set; }
        public int? PageSize { get; set; }
        public int? PartitionIndex { get; set; }

        // -------------------------------------------------
        // Source Ed-Fi ODS API processing context (shared)
        // -------------------------------------------------
        // public EdFiApiClient EdFiApiClient { get; set; }

        // ----------------------------
        // Global processing context
        // ----------------------------
        public ChangeWindow ChangeWindow { get; set; }
        public CancellationTokenSource CancellationSource { get; set; }

        // TODO: GKM - Need to eliminate use of JObject in signature of this factory method -- needs proper abstractions
        // Arguments: the page message, a single-read forward-only reader over the page JSON, and an optional
        // callback reporting the top-level array element count (see IProcessingBlocksFactory<T>.CreateProcessDataMessages)
        public Func<StreamResourcePageMessage<TProcessDataMessage>, TextReader, Action<int>, IEnumerable<TProcessDataMessage>> CreateProcessDataMessages { get; set; }

        /// <summary>
        /// Describes where this page sits in the source (offset/limit or cursor page token, partition bounds and
        /// change window) so that an item-level error can be traced back to the source request that produced it.
        /// Contains paging metadata only -- never document content -- so it is safe to log and to include in
        /// published error records.
        /// </summary>
        public string DescribeSourcePage()
        {
            var parts = new List<string>(6);

            if (Offset.HasValue)
            {
                parts.Add($"offset {Offset.Value}");
            }

            if (Limit.HasValue)
            {
                parts.Add($"limit {Limit.Value}");
            }

            if (PartitionIndex.HasValue)
            {
                parts.Add($"partition {PartitionIndex.Value}");
            }

            if (PageToken is not null)
            {
                parts.Add($"page token {PageToken}");
            }

            if (PageSize.HasValue)
            {
                parts.Add($"page size {PageSize.Value}");
            }

            if (PartitionFrom is not null || PartitionUntil is not null)
            {
                parts.Add($"partition from {PartitionFrom ?? "(start)"} until {PartitionUntil ?? "(end)"}");
            }

            if (ChangeWindow is not null)
            {
                parts.Add($"change versions {ChangeWindow.MinChangeVersion} to {ChangeWindow.MaxChangeVersion}");
            }

            return parts.Count == 0 ? "unknown page" : string.Join(", ", parts);
        }
    }
}
