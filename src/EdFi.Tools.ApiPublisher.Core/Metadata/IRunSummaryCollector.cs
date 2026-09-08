// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;

namespace EdFi.Tools.ApiPublisher.Core.Metadata
{
    /// <summary>
    /// Accumulates what a run did to each resource so that it can be reported when the run ends
    /// (see APIPUB-120). Implementations are shared across the whole run and written to concurrently.
    /// </summary>
    public interface IRunSummaryCollector
    {
        /// <summary>
        /// Records documents handed to the target for publishing. The stage is taken from the source resource
        /// URL's path suffix, since that is what identifies the stage at the point pages are streamed.
        /// </summary>
        /// <param name="sourceResourceUrl">The source resource URL, including the stage's path suffix.</param>
        /// <param name="count">The number of documents read from the page.</param>
        void AddAttemptedItems(string sourceResourceUrl, long count);

        /// <summary>
        /// Records an error reported during a stage. Errors against the target count against the resource;
        /// errors reading the source are counted separately, because the documents behind them were never
        /// attempted and their number is not known.
        /// </summary>
        void AddError(PublishingStage stage, ErrorItemMessage error);

        /// <summary>
        /// Records documents abandoned without being published or reported as errors, so that they are not
        /// counted as published.
        /// </summary>
        /// <param name="stage">The stage the documents were abandoned in.</param>
        /// <param name="resourceUrl">The resource URL, with or without the stage's path suffix.</param>
        /// <param name="count">The number of documents abandoned.</param>
        /// <param name="reason">A short operator-facing reason, reported with the run summary.</param>
        void AddSkippedItems(PublishingStage stage, string resourceUrl, long count, string reason);

        /// <summary>
        /// Gets the summary of the run so far, including the item counts the source reported.
        /// </summary>
        RunSummary GetSummary();
    }
}
