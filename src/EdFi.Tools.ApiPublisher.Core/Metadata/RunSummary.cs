// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Processing;
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
    /// <param name="AttemptedItemCount">The number of documents read from the source and handed to the target.</param>
    /// <param name="FailedItemCount">The number of documents the target rejected.</param>
    /// <param name="SkippedItemCount">The number of documents abandoned without being published or rejected.</param>
    /// <param name="PublishedItemCount">The number of documents the target accepted.</param>
    /// <param name="AuthorizationRetryPass">
    /// What the authorization retry pass did, when one ran for this resource. The counts above then describe
    /// that pass, because it re-publishes every document of the resource and therefore decides the outcome.
    /// </param>
    public record ResourceRunSummary(
        PublishingStage Stage,
        string ResourcePath,
        long? ExpectedItemCount,
        long AttemptedItemCount,
        long FailedItemCount,
        long SkippedItemCount,
        long PublishedItemCount,
        AuthorizationRetryPassSummary AuthorizationRetryPass = null)
    {
        /// <summary>
        /// The number of documents that were read from the source but whose outcome the run never learned,
        /// because it stopped before the target answered for them. Zero for a run that completed.
        /// </summary>
        public long UnresolvedItemCount =>
            AttemptedItemCount - PublishedItemCount - FailedItemCount - SkippedItemCount is var unresolved && unresolved > 0
                ? unresolved
                : 0;
    }

    /// <summary>
    /// What the authorization retry pass did to a resource: it re-publishes the whole resource after the
    /// resource's update prerequisites complete, so its outcome supersedes the first pass, and the first
    /// pass's failures are reported alongside to show what it recovered (see APIPUB-120).
    /// </summary>
    /// <param name="FirstPassFailedItemCount">The number of documents the target rejected on the first pass.</param>
    /// <param name="ReattemptedItemCount">The number of documents the retry pass published again.</param>
    /// <param name="FailedItemCount">The number of documents the target rejected on the retry pass.</param>
    public record AuthorizationRetryPassSummary(
        long FirstPassFailedItemCount,
        long ReattemptedItemCount,
        long FailedItemCount)
    {
        /// <summary>
        /// The number of documents the first pass reported as failed and the retry pass then published.
        /// Derived from the two passes' failure counts rather than from document identity, so it is a
        /// difference, not a list.
        /// </summary>
        public long RecoveredItemCount =>
            FirstPassFailedItemCount - FailedItemCount is var recovered && recovered > 0 ? recovered : 0;
    }
}
