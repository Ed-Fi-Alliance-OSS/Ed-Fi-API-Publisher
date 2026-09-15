// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Core.Processing.RunState;

/// <summary>
/// Follows each cursor-paged partition and records how far it got (see APIPUB-142).
/// </summary>
/// <remarks>
/// A page settles once it has no more documents to give and every document it gave has come back from the
/// target. A partition's mark then moves across settled pages one at a time, and only while they are
/// unbroken: the first settled page that lost a document stops the mark where it is, so a resumed run reads
/// that page again. A page whose documents never come back, which is what a run killed mid-flight leaves
/// behind, never settles, and stops the mark for the same reason.
/// <para>
/// The reporting methods run on the publishing pipeline's threads and do no I/O; the flush loop does the
/// writing. A settled page is dropped as the mark passes it and a stopped partition drops all of them, so
/// what is held is bounded by the pages the pipeline itself allows to be in flight.
/// </para>
/// </remarks>
public class PageCheckpointCoordinator : IPageCheckpointCoordinator
{
    /// <summary>How often progress is written while the run is publishing.</summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    private readonly IPublishRunStateStore _publishRunStateStore;

    private readonly ConcurrentDictionary<PartitionKey, PartitionProgress> _partitionsByKey = new();

    private readonly ILogger _logger = Log.ForContext(typeof(PageCheckpointCoordinator));

    private PublishRunState _runState;
    private CancellationTokenSource _flushCancellation;
    private Task _flushLoop;
    private int _pendingChanges;
    private bool _stopped;

    public PageCheckpointCoordinator(IPublishRunStateStore publishRunStateStore)
    {
        _publishRunStateStore = publishRunStateStore ?? throw new ArgumentNullException(nameof(publishRunStateStore));
    }

    public void Begin(PublishRunState runState)
    {
        ArgumentNullException.ThrowIfNull(runState);

        _runState = runState;

        // Owns its own token rather than taking the run's on purpose: a cancelled run is exactly the run whose
        // progress is worth writing, so the final write in StopAsync has to outlive the cancellation. Do not
        // link this to the run's token.
        _flushCancellation = new CancellationTokenSource();

        _flushLoop = RunFlushLoopAsync(_flushCancellation.Token);

        SeedResumePoints(runState);
    }

    public IReadOnlyList<string> TryGetResumeTokens(string resourceUrl, bool isAuthorizationRetryPass)
    {
        if (_runState is null)
        {
            return null;
        }

        var tokens = _partitionsByKey
            .Where(entry => entry.Key.IsAuthorizationRetryPass == isAuthorizationRetryPass
                && string.Equals(entry.Key.ResourceUrl, resourceUrl, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.Key.PartitionIndex)
            .Select(entry => entry.Value.ResumeFromPageToken)
            .ToList();

        // Only a partition seeded from a previous run carries a resume token, so a resource this run has
        // merely started reading never looks resumable. A recorded partition with no token would leave a
        // hole in the resource's ranges, so the whole resource goes back to asking the source.
        return tokens.Count == 0 || tokens.Any(string.IsNullOrEmpty) ? null : tokens;
    }

    /// <summary>
    /// Takes the previous run's progress as this run's starting points. The marks themselves are not carried
    /// over: this run numbers the pages it reads from one, and records what it confirms itself. A partition
    /// therefore reads again from the page it last confirmed, which re-publishes that page's documents -- safe,
    /// because publishing a document again is an upsert.
    /// </summary>
    private void SeedResumePoints(PublishRunState runState)
    {
        if (runState.Resources is null)
        {
            return;
        }

        bool seededAnything = false;

        foreach (var resource in runState.Resources)
        {
            foreach (var partition in resource.Partitions ?? new List<PublishRunPartitionState>())
            {
                var progress = GetPartition(
                    new PartitionKey(resource.ResourceUrl, resource.IsAuthorizationRetryPass, partition.PartitionIndex));

                lock (progress.SyncRoot)
                {
                    progress.ResumeFromPageToken = partition.LastCompletedPageToken ?? partition.StartingPageToken;
                    progress.StartingPageToken = progress.ResumeFromPageToken;
                }

                seededAnything = true;
            }
        }

        if (seededAnything)
        {
            // Written back as it now stands, so that a resumed run which dies before confirming anything
            // leaves the same starting points behind rather than a state rebuilt from the nothing it has seen
            Interlocked.Exchange(ref _pendingChanges, 1);
        }
    }

    public void PartitionsProduced(
        string resourceUrl,
        bool isAuthorizationRetryPass,
        IReadOnlyList<string> startingPageTokens)
    {
        if (_runState is null || startingPageTokens is null)
        {
            return;
        }

        for (int index = 0; index < startingPageTokens.Count; index++)
        {
            // The producer numbers the partitions it emits from the same list, 1-based (see APIPUB-139)
            var partition = GetPartition(new PartitionKey(resourceUrl, isAuthorizationRetryPass, index + 1));

            lock (partition.SyncRoot)
            {
                partition.StartingPageToken = startingPageTokens[index];
            }
        }

        Interlocked.Exchange(ref _pendingChanges, 1);
    }

    public void ItemProduced(SourcePageReference page)
    {
        if (_runState is null || page is null)
        {
            return;
        }

        var partition = GetPartition(PartitionKey.For(page));

        lock (partition.SyncRoot)
        {
            if (partition.IsStopped)
            {
                return;
            }

            GetPage(partition, page).ProducedCount++;
        }
    }

    public void PageFullyRead(SourcePageReference page)
    {
        if (_runState is null || page is null)
        {
            return;
        }

        var partition = GetPartition(PartitionKey.For(page));

        lock (partition.SyncRoot)
        {
            if (partition.IsStopped)
            {
                return;
            }

            GetPage(partition, page).IsFullyRead = true;

            AdvanceMark(partition);
        }
    }

    public void ItemCompleted(SourcePageReference page, bool lost)
    {
        if (_runState is null || page is null)
        {
            return;
        }

        var partition = GetPartition(PartitionKey.For(page));

        lock (partition.SyncRoot)
        {
            if (partition.IsStopped)
            {
                return;
            }

            var pageProgress = GetPage(partition, page);

            pageProgress.CompletedCount++;

            if (lost)
            {
                pageProgress.LostDocument = true;
            }

            AdvanceMark(partition);
        }
    }

    public async Task StopAsync()
    {
        // Called once where the run decides its outcome and again from its finally, so that a run which broke
        // still writes what it reached. The second call must do nothing: writing again after a clean run has
        // removed its state would put the state back, and the next resume would replay a window already
        // published (see APIPUB-142).
        if (_runState is null || _stopped)
        {
            return;
        }

        _stopped = true;

        _flushCancellation.Cancel();

        try
        {
            await _flushLoop.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The loop swallows its own write failures; anything surfacing here is unexpected and still not
            // worth failing a run over, since the run's outcome does not depend on its progress being written
            _logger.Warning(ex, "The run state flush loop ended unexpectedly.");
        }

        await FlushAsync(CancellationToken.None).ConfigureAwait(false);

        _flushCancellation.Dispose();
    }

    /// <summary>
    /// Moves the partition's mark across settled pages, one at a time and only while they are unbroken.
    /// Called with the partition's lock held.
    /// </summary>
    private void AdvanceMark(PartitionProgress partition)
    {
        while (partition.Pages.TryGetValue(partition.MarkedPageNumber + 1, out var nextPage) && nextPage.IsSettled)
        {
            if (nextPage.LostDocument)
            {
                // Everything behind this page is still correct, and everything from it onwards has to be read
                // again, so there is nothing further to follow for this partition
                partition.IsStopped = true;
                partition.Pages.Clear();

                _logger.Debug(
                    "{ResourceUrl}: partition {PartitionIndex} will resume from page {PageNumber}, which lost a document.",
                    partition.Key.ResourceUrl, partition.Key.PartitionIndex, partition.MarkedPageNumber + 1);

                return;
            }

            partition.MarkedPageNumber++;
            partition.MarkedPageToken = nextPage.PageToken;
            partition.Pages.Remove(partition.MarkedPageNumber);

            Interlocked.Exchange(ref _pendingChanges, 1);
        }
    }

    private PartitionProgress GetPartition(PartitionKey key)
        => _partitionsByKey.GetOrAdd(key, k => new PartitionProgress(k));

    /// <summary>Gets the page's progress, with the partition's lock held.</summary>
    private static PageProgress GetPage(PartitionProgress partition, SourcePageReference page)
    {
        if (!partition.Pages.TryGetValue(page.PartitionPageNumber, out var pageProgress))
        {
            pageProgress = new PageProgress { PageToken = page.PageToken };
            partition.Pages.Add(page.PartitionPageNumber, pageProgress);
        }

        return pageProgress;
    }

    private async Task RunFlushLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(FlushInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes what has been marked so far. Skipped when no mark has moved since the last write, so a run
    /// whose partitions are all stalled or finished stops rewriting the same file.
    /// </summary>
    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _pendingChanges, 0) == 0)
        {
            return;
        }

        _runState.Resources = BuildResourceStates();

        await _publishRunStateStore.SaveAsync(_runState, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes a snapshot of every partition's mark, ordered so that the file reads the same way twice.
    /// </summary>
    private List<PublishRunResourceState> BuildResourceStates()
    {
        var partitionStatesByResource = new Dictionary<(string ResourceUrl, bool IsAuthorizationRetryPass), List<PublishRunPartitionState>>();

        foreach (var partition in _partitionsByKey.Values)
        {
            PublishRunPartitionState partitionState;

            lock (partition.SyncRoot)
            {
                partitionState = new PublishRunPartitionState
                {
                    PartitionIndex = partition.Key.PartitionIndex,
                    StartingPageToken = partition.StartingPageToken,
                    LastCompletedPageToken = partition.MarkedPageToken,
                    LastCompletedPageNumber = partition.MarkedPageNumber,
                };
            }

            var resourceKey = (partition.Key.ResourceUrl, partition.Key.IsAuthorizationRetryPass);

            if (!partitionStatesByResource.TryGetValue(resourceKey, out var partitionStates))
            {
                partitionStates = new List<PublishRunPartitionState>();
                partitionStatesByResource.Add(resourceKey, partitionStates);
            }

            partitionStates.Add(partitionState);
        }

        return partitionStatesByResource
            .OrderBy(entry => entry.Key.ResourceUrl, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Key.IsAuthorizationRetryPass)
            .Select(
                entry => new PublishRunResourceState
                {
                    ResourceUrl = entry.Key.ResourceUrl,
                    IsAuthorizationRetryPass = entry.Key.IsAuthorizationRetryPass,
                    Partitions = entry.Value.OrderBy(partition => partition.PartitionIndex).ToList(),
                })
            .ToList();
    }

    private readonly record struct PartitionKey(string ResourceUrl, bool IsAuthorizationRetryPass, int PartitionIndex)
    {
        public static PartitionKey For(SourcePageReference page)
            => new(page.ResourceUrl, page.IsAuthorizationRetryPass, page.PartitionIndex);
    }

    private sealed class PartitionProgress
    {
        public PartitionProgress(PartitionKey key)
        {
            Key = key;
        }

        public PartitionKey Key { get; }

        /// <summary>Guards everything below, including <see cref="Pages" />.</summary>
        public object SyncRoot { get; } = new();

        public string StartingPageToken { get; set; }

        /// <summary>
        /// Where a previous run left this partition, set only when seeding from a resumed state. Kept apart
        /// from <see cref="StartingPageToken" /> so that a partition this run merely started reading is never
        /// mistaken for one that can be resumed.
        /// </summary>
        public string ResumeFromPageToken { get; set; }

        public int MarkedPageNumber { get; set; }

        public string MarkedPageToken { get; set; }

        /// <summary>Set once a page of this partition lost a document; the mark never moves again.</summary>
        public bool IsStopped { get; set; }

        public Dictionary<int, PageProgress> Pages { get; } = new();
    }

    private sealed class PageProgress
    {
        public string PageToken { get; set; }

        public long ProducedCount { get; set; }

        public long CompletedCount { get; set; }

        public bool IsFullyRead { get; set; }

        public bool LostDocument { get; set; }

        /// <summary>
        /// Whether the page has no more documents to give and every one it gave has come back. Documents are
        /// counted as they are handed over rather than from the page's item count, so a completion that
        /// arrives before the page finishes being read cannot settle it early.
        /// </summary>
        public bool IsSettled => IsFullyRead && CompletedCount >= ProducedCount;
    }
}
