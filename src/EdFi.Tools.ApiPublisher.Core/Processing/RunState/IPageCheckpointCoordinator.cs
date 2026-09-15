// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Core.Processing.RunState;

/// <summary>
/// Follows how far each cursor-paged partition has got and records it, so that a run which fails partway
/// can resume after the pages it is certain about (see APIPUB-142).
/// </summary>
/// <remarks>
/// A page is behind the mark once every document it produced has come back from the target and none of
/// them was lost, and a partition's mark only ever moves across an unbroken run of such pages. The first
/// page that loses a document stops that partition's mark where it is, which is what makes the resumed
/// run read that page again.
/// <para>
/// Everything here is called from the publishing pipeline while it runs, on many threads at once, and does
/// no I/O: writing is the flush loop's job.
/// </para>
/// </remarks>
public interface IPageCheckpointCoordinator
{
    /// <summary>
    /// Attaches the coordinator to the run's state and starts writing progress to it. Until this is called,
    /// everything reported is ignored, which is what keeps a run that is not checkpointing free of the cost.
    /// </summary>
    /// <remarks>
    /// Progress already in the state is taken as the point this run carries on from, which is how a resumed
    /// run both starts in the right place and keeps the marks of resources it has not reached yet: the state
    /// is rebuilt from what the coordinator holds every time it is written. A run that is not resuming
    /// arrives here with nothing recorded.
    /// </remarks>
    void Begin(PublishRunState runState, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the page tokens a resumed run should walk for one pass over one resource, in partition order, or
    /// null when the previous run recorded nothing for it and the source should be asked for partitions as
    /// usual. A partition that confirmed at least one page resumes at that page, which is read again and then
    /// walked on from; one that confirmed none resumes where the source originally said it starts.
    /// </summary>
    IReadOnlyList<string> TryGetResumeTokens(string resourceUrl, bool isAuthorizationRetryPass);

    /// <summary>
    /// Records the partition tokens the source handed back for one pass over one resource, in the order it
    /// returned them, so that a resume can replay them instead of asking for partitions again.
    /// </summary>
    void PartitionsProduced(
        string resourceUrl,
        bool isAuthorizationRetryPass,
        IReadOnlyList<string> startingPageTokens);

    /// <summary>Records that one more document of the page has been handed to the target side.</summary>
    void ItemProduced(SourcePageReference page);

    /// <summary>
    /// Records that the page has no more documents to give, so it can settle once the ones already handed
    /// over have come back.
    /// </summary>
    void PageFullyRead(SourcePageReference page);

    /// <summary>
    /// Records that one document of the page has reached a terminal outcome. <paramref name="lost" /> is
    /// true when the target did not take it, which stops that partition's mark at the page before it.
    /// </summary>
    void ItemCompleted(SourcePageReference page, bool lost);

    /// <summary>
    /// Stops the flush loop and writes what has been recorded one last time.
    /// </summary>
    Task StopAsync();
}
