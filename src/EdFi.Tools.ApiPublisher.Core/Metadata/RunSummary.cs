// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Processing;
using System;
using System.Collections.Generic;

namespace EdFi.Tools.ApiPublisher.Core.Metadata
{
    /// <summary>
    /// What a publishing run did to each resource, so that the outcome of a run can be reported without
    /// reading the log (see APIPUB-120).
    /// </summary>
    /// <param name="Resources">One entry per resource that took part in a stage of the run.</param>
    /// <param name="SourceReadErrorCount">
    /// The number of errors reported while reading from the source (a failed page or item count, rather than
    /// a rejected document). Documents behind these errors were never attempted, which is why they are
    /// reported apart from the per-resource counts.
    /// </param>
    /// <param name="SkipReasons">The distinct reasons documents were skipped during the run.</param>
    public record RunSummary(
        IReadOnlyList<ResourceRunSummary> Resources,
        long SourceReadErrorCount,
        IReadOnlyList<string> SkipReasons);

    /// <summary>
    /// What a publishing run did to one resource within one stage.
    /// </summary>
    /// <param name="Stage">The stage the resource was processed in.</param>
    /// <param name="ResourcePath">The resource path, without the stage's path suffix.</param>
    /// <param name="ExpectedItemCount">
    /// The number of items the source reported for the resource, or <c>null</c> when the source could not
    /// report one.
    /// </param>
    /// <param name="AttemptedItemCount">The number of documents handed to the target for publishing.</param>
    /// <param name="FailedItemCount">The number of documents the target rejected.</param>
    /// <param name="SkippedItemCount">The number of documents abandoned without being published or rejected.</param>
    public record ResourceRunSummary(
        PublishingStage Stage,
        string ResourcePath,
        long? ExpectedItemCount,
        long AttemptedItemCount,
        long FailedItemCount,
        long SkippedItemCount)
    {
        /// <summary>
        /// The number of documents the target is expected to hold as a result of this run. It is derived, not
        /// observed: the publishing pipeline reports errors, not successes, so a document that was attempted
        /// and neither rejected nor skipped is counted as published.
        /// </summary>
        public long PublishedItemCount => Math.Max(0, AttemptedItemCount - FailedItemCount - SkippedItemCount);
    }
}
