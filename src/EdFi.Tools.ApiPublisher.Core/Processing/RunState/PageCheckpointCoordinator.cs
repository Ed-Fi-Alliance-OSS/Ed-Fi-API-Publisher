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

    public PageCheckpointCoordinator(IPublishRunStateStore publishRunStateStore)
    {
        _publishRunStateStore = publishRunStateStore ?? throw new ArgumentNullException(nameof(publishRunStateStore));
    }

    public void Begin(PublishRunState runState, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runState);

        _runState = runState;

        // Not linked to the run's token: a cancelled run is exactly when the progress it reached is worth
        // writing, so the final write in StopAsync has to outlive the cancellation
        _flushCancellation = new CancellationTokenSource();

        _flushLoop = RunFlushLoopAsync(_flushCancellation.Token);
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
        if (_runState is null)
        {
            return;
        }

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
