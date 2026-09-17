// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using EdFi.Tools.ApiPublisher.Core.Processing.RunState;
using NUnit.Framework;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// APIPUB-142: a partition's mark moves only across pages whose documents all reached the target, so
    /// that a resumed run reads again from the first page that did not.
    /// </summary>
    [TestFixture]
    public class PageCheckpointCoordinatorTests
    {
        private const string StudentsUrl = "/ed-fi/students";

        [Test]
        public async Task A_page_whose_documents_all_reach_the_target_moves_the_mark()
        {
            var (coordinator, store, runState) = StartCoordinator();

            var page = Page(pageNumber: 1, token: "TOKEN-1");

            ReadPage(coordinator, page, documentCount: 2);
            CompleteDocuments(coordinator, page, count: 2);

            await coordinator.StopAsync();

            store.SaveCount.ShouldBeGreaterThan(0);

            var partition = SinglePartition(runState);
            partition.LastCompletedPageToken.ShouldBe("TOKEN-1");
            partition.LastCompletedPageNumber.ShouldBe(1);
        }

        [Test]
        public async Task A_page_that_lost_a_document_stops_the_mark_at_the_page_before_it()
        {
            var (coordinator, _, runState) = StartCoordinator();

            var firstPage = Page(pageNumber: 1, token: "TOKEN-1");
            var secondPage = Page(pageNumber: 2, token: "TOKEN-2");
            var thirdPage = Page(pageNumber: 3, token: "TOKEN-3");

            ReadPage(coordinator, firstPage, documentCount: 1);
            CompleteDocuments(coordinator, firstPage, count: 1);

            ReadPage(coordinator, secondPage, documentCount: 2);
            coordinator.ItemCompleted(secondPage, lost: false);
            coordinator.ItemCompleted(secondPage, lost: true);

            // Even a page after it that went perfectly must not move the mark past the loss
            ReadPage(coordinator, thirdPage, documentCount: 1);
            CompleteDocuments(coordinator, thirdPage, count: 1);

            await coordinator.StopAsync();

            var partition = SinglePartition(runState);
            partition.LastCompletedPageToken.ShouldBe("TOKEN-1");
            partition.LastCompletedPageNumber.ShouldBe(1);
        }

        [Test]
        public async Task A_page_whose_documents_never_all_come_back_does_not_move_the_mark()
        {
            var (coordinator, _, runState) = StartCoordinator();

            // The producer records the partition before its first page is read, so a partition that never
            // settles a page is still resumable from where the source said it starts
            coordinator.PartitionsProduced(StudentsUrl, isAuthorizationRetryPass: false, new[] { "PARTITION-START" });

            var page = Page(pageNumber: 1, token: "TOKEN-1");

            // What a run killed mid-flight leaves behind: the page was read, one of its two documents never
            // reached a terminal outcome
            ReadPage(coordinator, page, documentCount: 2);
            CompleteDocuments(coordinator, page, count: 1);

            await coordinator.StopAsync();

            var partition = SinglePartition(runState);
            partition.StartingPageToken.ShouldBe("PARTITION-START");
            partition.LastCompletedPageToken.ShouldBeNull();
        }

        [Test]
        public async Task The_two_passes_over_a_resource_keep_their_own_marks()
        {
            var (coordinator, _, runState) = StartCoordinator();

            var firstPassPage = Page(pageNumber: 1, token: "FIRST-PASS", isAuthorizationRetryPass: false);
            var retryPassPage = Page(pageNumber: 1, token: "RETRY-PASS", isAuthorizationRetryPass: true);

            ReadPage(coordinator, firstPassPage, documentCount: 1);
            CompleteDocuments(coordinator, firstPassPage, count: 1);

            ReadPage(coordinator, retryPassPage, documentCount: 1);
            CompleteDocuments(coordinator, retryPassPage, count: 1);

            await coordinator.StopAsync();

            runState.Resources.Count.ShouldBe(2);

            runState.FindResource(StudentsUrl, isAuthorizationRetryPass: false)
                .Partitions.Single().LastCompletedPageToken.ShouldBe("FIRST-PASS");

            runState.FindResource(StudentsUrl, isAuthorizationRetryPass: true)
                .Partitions.Single().LastCompletedPageToken.ShouldBe("RETRY-PASS");
        }

        [Test]
        public async Task The_partition_tokens_the_source_returned_are_recorded_in_order()
        {
            var (coordinator, _, runState) = StartCoordinator();

            coordinator.PartitionsProduced(StudentsUrl, isAuthorizationRetryPass: false, new[] { "P1", "P2", "P3" });

            await coordinator.StopAsync();

            var partitions = runState.FindResource(StudentsUrl, isAuthorizationRetryPass: false).Partitions;

            partitions.Select(p => p.PartitionIndex).ShouldBe(new[] { 1, 2, 3 });
            partitions.Select(p => p.StartingPageToken).ShouldBe(new[] { "P1", "P2", "P3" });
        }

        [Test]
        public async Task Nothing_is_recorded_for_an_offset_paged_read()
        {
            var (coordinator, store, runState) = StartCoordinator();

            // An offset-paged item carries no page reference at all
            coordinator.ItemProduced(null);
            coordinator.PageFullyRead(null);
            coordinator.ItemCompleted(null, lost: false);

            await coordinator.StopAsync();

            runState.Resources.ShouldBeEmpty();
            store.SaveCount.ShouldBe(0);
        }

        /// <summary>
        /// The state is rebuilt from what the coordinator holds each time it is written, so a resumed run
        /// has to carry the previous run's positions forward or it would erase the marks of every resource
        /// it has not reached yet.
        /// </summary>
        [Test]
        public async Task A_resumed_run_keeps_the_positions_of_resources_it_has_not_reached()
        {
            var store = new CapturingRunStateStore();
            var coordinator = new PageCheckpointCoordinator(store);

            var runState = PublishRunState.StartNew("TestSource", "TestTarget", changeWindow: null);
            runState.Resources.Add(
                new PublishRunResourceState
                {
                    ResourceUrl = StudentsUrl,
                    Partitions =
                    {
                        new PublishRunPartitionState { PartitionIndex = 1, StartingPageToken = "s1", LastCompletedPageToken = "s1-page7" },
                    },
                });
            runState.Resources.Add(
                new PublishRunResourceState
                {
                    ResourceUrl = "/ed-fi/staffs",
                    Partitions = { new PublishRunPartitionState { PartitionIndex = 1, StartingPageToken = "f1" } },
                });

            coordinator.Begin(runState);

            // The run ends without reaching either resource
            await coordinator.StopAsync();

            runState.Resources.Count.ShouldBe(2);

            var students = runState.FindResource(StudentsUrl, isAuthorizationRetryPass: false).Partitions.Single();
            students.StartingPageToken.ShouldBe("s1-page7");
            students.LastCompletedPageToken.ShouldBeNull();

            runState.FindResource("/ed-fi/staffs", isAuthorizationRetryPass: false)
                .Partitions.Single().StartingPageToken.ShouldBe("f1");
        }

        [Test]
        public void A_resource_with_no_recorded_position_is_not_resumable()
        {
            var (coordinator, _, _) = StartCoordinator();

            coordinator.TryGetResumeTokens(StudentsUrl, isAuthorizationRetryPass: false).ShouldBeNull();

            // Reading a resource this run does not make it look like something to resume from
            var page = Page(pageNumber: 1, token: "TOKEN-1");
            ReadPage(coordinator, page, documentCount: 1);
            CompleteDocuments(coordinator, page, count: 1);

            coordinator.TryGetResumeTokens(StudentsUrl, isAuthorizationRetryPass: false).ShouldBeNull();
        }

        /// <summary>
        /// The case where a resume is refused for one resource but its recorded partitions are already in
        /// hand: the source is asked to partition it afresh, and what it hands back is the whole of what the
        /// resource is being read as. A mark from the run before must not survive into a range that replaced
        /// the one it was taken in, and a partition beyond the new count must not be written back at all.
        /// </summary>
        [Test]
        public async Task Repartitioning_a_resource_replaces_every_position_it_had_recorded()
        {
            var store = new CapturingRunStateStore();
            var coordinator = new PageCheckpointCoordinator(store);

            var runState = PublishRunState.StartNew("TestSource", "TestTarget", changeWindow: null);
            runState.Resources.Add(
                new PublishRunResourceState
                {
                    ResourceUrl = StudentsUrl,
                    Partitions =
                    {
                        new PublishRunPartitionState { PartitionIndex = 1, StartingPageToken = "old-1", LastCompletedPageToken = "old-1-page5", LastCompletedPageNumber = 5 },

                        // No token of any kind, which is what makes the whole resource unresumable
                        new PublishRunPartitionState { PartitionIndex = 2 },
                        new PublishRunPartitionState { PartitionIndex = 3, StartingPageToken = "old-3", LastCompletedPageToken = "old-3-page9", LastCompletedPageNumber = 9 },
                    },
                });

            coordinator.Begin(runState);

            coordinator.TryGetResumeTokens(StudentsUrl, isAuthorizationRetryPass: false).ShouldBeNull();

            // The source is asked again and hands back fewer ranges than the run before was given
            coordinator.PartitionsProduced(StudentsUrl, isAuthorizationRetryPass: false, new[] { "new-1", "new-2" });

            var page = Page(pageNumber: 1, token: "new-1");
            ReadPage(coordinator, page, documentCount: 1);
            CompleteDocuments(coordinator, page, count: 1);

            await coordinator.StopAsync();

            var partitions = runState.FindResource(StudentsUrl, isAuthorizationRetryPass: false).Partitions;

            // The third range is gone: this run never read it, and replaying it later would overlap the two
            // that replaced it
            partitions.Count.ShouldBe(2);

            partitions[0].StartingPageToken.ShouldBe("new-1");
            partitions[0].LastCompletedPageToken.ShouldBe("new-1");
            partitions[0].LastCompletedPageNumber.ShouldBe(1);

            partitions[1].StartingPageToken.ShouldBe("new-2");
            partitions[1].LastCompletedPageToken.ShouldBeNull();
        }

        /// <summary>
        /// A write the store could not make must leave its work pending. Without that, a write is skipped
        /// whenever no mark has moved since the last one, so the write on the way out of a failed run finds
        /// nothing to do and everything marked since the last write that landed is never recorded.
        /// </summary>
        [Test]
        public async Task A_write_the_store_refuses_leaves_its_progress_to_be_written_again()
        {
            var store = new CapturingRunStateStore { WriteSucceeds = false };
            var coordinator = new PageCheckpointCoordinator(store, flushInterval: TimeSpan.FromMilliseconds(20));
            var runState = PublishRunState.StartNew("TestSource", "TestTarget", changeWindow: null);

            coordinator.Begin(runState);

            var page = Page(pageNumber: 1, token: "TOKEN-1");
            ReadPage(coordinator, page, documentCount: 1);
            CompleteDocuments(coordinator, page, count: 1);

            // Nothing is marked from here on, so every further write is the refused one being carried
            // forward. Held pending it is attempted again; dropped it would leave the store never asked again.
            var givingUp = DateTime.UtcNow.AddSeconds(5);

            while (store.SaveCount < 3 && DateTime.UtcNow < givingUp)
            {
                await Task.Delay(10);
            }

            await coordinator.StopAsync();

            store.SaveCount.ShouldBeGreaterThanOrEqualTo(3);
        }

        private static (PageCheckpointCoordinator, CapturingRunStateStore, PublishRunState) StartCoordinator()
        {
            var store = new CapturingRunStateStore();
            var coordinator = new PageCheckpointCoordinator(store);
            var runState = PublishRunState.StartNew("TestSource", "TestTarget", changeWindow: null);

            coordinator.Begin(runState);

            return (coordinator, store, runState);
        }

        private static SourcePageReference Page(int pageNumber, string token, bool isAuthorizationRetryPass = false)
            => new(StudentsUrl, isAuthorizationRetryPass, PartitionIndex: 1, pageNumber, token);

        /// <summary>Hands over the page's documents and says the page has no more to give.</summary>
        private static void ReadPage(PageCheckpointCoordinator coordinator, SourcePageReference page, int documentCount)
        {
            for (int i = 0; i < documentCount; i++)
            {
                coordinator.ItemProduced(page);
            }

            coordinator.PageFullyRead(page);
        }

        private static void CompleteDocuments(PageCheckpointCoordinator coordinator, SourcePageReference page, int count)
        {
            for (int i = 0; i < count; i++)
            {
                coordinator.ItemCompleted(page, lost: false);
            }
        }

        private static PublishRunPartitionState SinglePartition(PublishRunState runState)
            => runState.Resources.Single().Partitions.Single();

        private sealed class CapturingRunStateStore : IPublishRunStateStore
        {
            public int SaveCount { get; private set; }

            public string Location => "(in memory)";

            public Task<PublishRunState> TryLoadAsync(CancellationToken cancellationToken)
                => Task.FromResult<PublishRunState>(null);

            /// <summary>Set to false to stand in for a store that cannot write.</summary>
            public bool WriteSucceeds { get; set; } = true;

            public Task<bool> SaveAsync(PublishRunState state, CancellationToken cancellationToken)
            {
                SaveCount++;

                return Task.FromResult(WriteSucceeds);
            }

            public Task DeleteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }
    }
}
