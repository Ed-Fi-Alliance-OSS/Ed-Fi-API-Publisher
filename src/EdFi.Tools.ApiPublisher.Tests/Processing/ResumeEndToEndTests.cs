// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Metadata;
using EdFi.Tools.ApiPublisher.Core.Processing.RunState;
using EdFi.Tools.ApiPublisher.Tests.Extensions;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Shouldly;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// APIPUB-142 through the full ChangeProcessor pipeline: a run that loses a document leaves each
    /// partition marked at the last page it confirmed, and the run that resumes it walks on from those marks
    /// without asking the source to partition the resource again.
    /// </summary>
    [TestFixture]
    public class ResumeEndToEndTests
    {
        private const string Resource = "/ed-fi/stateEducationAgencies";
        private const int PageSize = 25;
        private const int PagesPerPartition = 4;
        private const int Partitions = 2;
        private const int TotalItems = Partitions * PagesPerPartition * PageSize;

        /// <summary>The document the target rejects, which sits on page 2 of partition 1.</summary>
        private const string RejectedStamp = "item-25";

        [Test]
        public async Task A_resumed_run_should_walk_on_from_the_marks_without_re_partitioning()
        {
            TestHelpers.InitializeLogging();

            var store = new InMemoryRunStateStore();

            // First run: the target rejects one document on page 2 of partition 1, so that partition's mark
            // stops at page 1 while partition 2 is confirmed all the way through
            var firstRun = await RunAsync(store, resume: false, rejectedStamp: RejectedStamp);

            // A run that lost a document keeps its state: that is the whole point of it. Whether such a run
            // also ends by throwing is the error-tolerance question APIPUB-120 answers, not this one.
            store.State.ShouldNotBeNull(
                "a run that lost a document must leave its state behind; page tokens read: "
                + string.Join(",", firstRun.PageTokens.OrderBy(t => t)));

            var firstPartition = PartitionOf(store.State, 1);
            var secondPartition = PartitionOf(store.State, 2);

            firstPartition.LastCompletedPageToken.ShouldBe("p1-1");
            secondPartition.LastCompletedPageToken.ShouldBe($"p2-{PagesPerPartition}");

            // Second run: nothing is rejected, and it continues from those marks
            var secondRun = await RunAsync(store, resume: true, rejectedStamp: null);

            secondRun.Failed.ShouldBeFalse();

            // The detection probe asks for one partition and still runs on a resumed run, because the source
            // has to be confirmed as still supporting cursor paging. What must not happen again is the
            // producer's own request, which asks for as many partitions as the run is configured for.
            firstRun.PartitionsNumbers.OrderBy(n => n).ShouldBe(new[] { "1", Partitions.ToString() });

            secondRun.PartitionsNumbers.ShouldBe(
                new[] { "1" },
                "a resumed resource walks the ranges the previous run was given, so only the detection probe should reach /partitions");

            // Partition 1 reads its marked page again and everything after it; partition 2 reads only its
            // marked page and the empty page that ends the walk. Page 1 of partition 2 is the page the resume
            // does not read again.
            secondRun.PageTokens.OrderBy(t => t).ShouldBe(
                new[] { "p1-1", "p1-2", "p1-3", "p1-4", "p1-5", $"p2-{PagesPerPartition}", $"p2-{PagesPerPartition + 1}" });

            secondRun.PageTokens.ShouldNotContain("p2-1");

            // A clean run has nothing left to resume
            store.State.ShouldBeNull(secondRun.Summary);
        }

        /// <summary>
        /// The pinned window is what replaced the snapshot identity the original design asked for: on 7.x the
        /// publisher reads through a boolean Use-Snapshot header and is given no snapshot to pin, so the
        /// window is what keeps a resumed run to the same scope of work.
        /// </summary>
        [Test]
        public async Task A_resumed_run_should_replay_the_change_window_instead_of_computing_a_new_one()
        {
            TestHelpers.InitializeLogging();

            var store = new InMemoryRunStateStore();

            var firstRun = await RunAsync(store, resume: false, rejectedStamp: RejectedStamp, availableChangeVersions: 1100);

            firstRun.CountWindows.ShouldAllBe(w => w == "1001..1100");
            store.State.MaxChangeVersion.ShouldBe(1100);

            // The source moves on between the two runs, as it would in the field
            var secondRun = await RunAsync(store, resume: true, rejectedStamp: null, availableChangeVersions: 2000);

            // A resumed run reads the window that was in force when the run it continues started, not a wider
            // one ending at the source's new change version
            secondRun.CountWindows.ShouldAllBe(w => w == "1001..1100");
        }

        [Test]
        public async Task A_state_written_for_another_source_should_be_refused_and_the_run_should_start_over()
        {
            TestHelpers.InitializeLogging();

            var store = new InMemoryRunStateStore();

            await RunAsync(store, resume: false, rejectedStamp: RejectedStamp);

            store.State.ShouldNotBeNull();

            // What an operator pointing a resume at the wrong state file would have
            store.State.SourceConnectionName = "SomeOtherSource";

            var secondRun = await RunAsync(store, resume: true, rejectedStamp: null);

            // A refused resume asks the source to partition the resource again, as a run from the beginning does
            secondRun.PartitionsNumbers.OrderBy(n => n).ShouldBe(new[] { "1", Partitions.ToString() });

            secondRun.PageTokens.ShouldContain("p2-1", "a refused resume reads the resource in full");
        }

        private static PublishRunPartitionState PartitionOf(PublishRunState state, int partitionIndex)
            => state.FindResource(Resource, isAuthorizationRetryPass: false)
                .Partitions.Single(p => p.PartitionIndex == partitionIndex);

        private sealed record RunOutcome(
            bool Failed,
            IReadOnlyList<string> PageTokens,
            IReadOnlyList<string> PartitionsNumbers,
            IReadOnlyList<string> CountWindows,
            string Summary);

        private static async Task<RunOutcome> RunAsync(
            InMemoryRunStateStore store,
            bool resume,
            string rejectedStamp,
            int availableChangeVersions = 1100)
        {
            var resourceFaker = TestHelpers.GetGenericResourceFaker();
            var pageTokens = new ConcurrentBag<string>();
            var partitionsNumbers = new ConcurrentBag<string>();
            var countWindows = new ConcurrentBag<string>();

            var source = TestHelpers.GetFakeBaselineSourceApiRequestHandler()
                .ApiVersionMetadata(apiVersion: "7.3")
                .AvailableChangeVersions(availableChangeVersions)
                .ResourceCount(responseTotalCountHeader: TotalItems);

            // The count request carries the window the run is actually reading, and is the one request a
            // resumed run still makes, so it is where the pinned window can be observed
            A.CallTo(
                    () => source.Get(
                        A<string>.Ignored,
                        A<HttpRequestMessage>.That.Matches(
                            msg => msg.RequestUri.ParseQueryString()["totalCount"] == "true"
                                && msg.RequestUri.LocalPath.EndsWith(Resource))))
                .ReturnsLazily(
                    (string _, HttpRequestMessage request) =>
                    {
                        var query = request.RequestUri.ParseQueryString();
                        countWindows.Add(query["minChangeVersion"] + ".." + query["maxChangeVersion"]);

                        return FakeResponse.OK("[]").AppendHeaders(("Total-Count", TotalItems.ToString()));
                    });

            A.CallTo(
                    () => source.Get(
                        A<string>.Ignored,
                        A<HttpRequestMessage>.That.Matches(msg => msg.RequestUri.LocalPath.EndsWith($"{Resource}/partitions"))))
                .ReturnsLazily(
                    (string _, HttpRequestMessage request) =>
                    {
                        partitionsNumbers.Add(request.RequestUri.ParseQueryString()["number"]);

                        return FakeResponse.OK(
                            new { pageTokens = Enumerable.Range(1, Partitions).Select(p => $"p{p}-1").ToArray() });
                    });

            A.CallTo(
                    () => source.Get(
                        A<string>.Ignored,
                        A<HttpRequestMessage>.That.Matches(
                            msg => msg.RequestUri.LocalPath.EndsWith(Resource)
                                && msg.RequestUri.ParseQueryString()["totalCount"] != "true")))
                .ReturnsLazily(
                    call =>
                    {
                        var request = (HttpRequestMessage)call.Arguments[1];
                        string token = request.RequestUri.ParseQueryString()["pageToken"];

                        pageTokens.Add(token);

                        var parts = token.Substring(1).Split('-').Select(int.Parse).ToArray();
                        int partition = parts[0], page = parts[1];

                        // The page past the end of a partition is empty and carries no Next-Page-Token, which
                        // is what ends the walk
                        if (page > PagesPerPartition)
                        {
                            return FakeResponse.OK("[]");
                        }

                        var items = resourceFaker.Generate(PageSize);

                        for (int i = 0; i < items.Count; i++)
                        {
                            items[i].VehicleManufacturer =
                                $"item-{((((partition - 1) * PagesPerPartition) + (page - 1)) * PageSize) + i}";
                        }

                        return FakeResponse.OK(items).AppendHeaders(("Next-Page-Token", $"p{partition}-{page + 1}"));
                    });

            // The deletes and keyChanges stages run on an incremental window and are offset-paged, so they are
            // not resumed. Served as empty here so that a clean run really is clean: an unserved endpoint
            // counts as a source read error, which would itself keep the run's state alive.
            A.CallTo(
                    () => source.Get(
                        A<string>.Ignored,
                        A<HttpRequestMessage>.That.Matches(
                            msg => msg.RequestUri.LocalPath.EndsWith("/deletes")
                                || msg.RequestUri.LocalPath.EndsWith("/keyChanges"))))
                .ReturnsLazily(() => FakeResponse.OK("[]").AppendHeaders(("Total-Count", "0")));

            var target = TestHelpers.GetFakeBaselineTargetApiRequestHandler();

            A.CallTo(() => target.Post(A<string>.That.Matches(url => url.EndsWith(Resource)), A<HttpRequestMessage>.Ignored))
                .ReturnsLazily(
                    call =>
                    {
                        var request = (HttpRequestMessage)call.Arguments[1];
                        string body = request.Content!.ReadAsStringAsync().Result;
                        string stamp = JObject.Parse(body)["vehicleManufacturer"]!.Value<string>()!;

                        return stamp == rejectedStamp
                            ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"message\":\"rejected by the test\"}") }
                            : new HttpResponseMessage(HttpStatusCode.OK);
                    });

            var options = TestHelpers.GetOptions();
            options.IncludeDescriptors = false;
            options.StreamingPageSize = PageSize;
            options.MaxDegreeOfParallelismForStreamResourcePages = Partitions;
            options.CursorPagingPartitionCount = Partitions;
            options.ResumeLastRun = resume;

            var coordinator = new PageCheckpointCoordinator(store);

            var metadataCollector = new PublishingOperationMetadataCollector();
            var summaryCollector = new RunSummaryCollector(metadataCollector);

            var changeProcessor = TestHelpers.CreateChangeProcessorWithDefaultDependencies(
                options,
                TestHelpers.GetSourceApiConnectionDetails(include: new[] { Resource }),
                source,
                TestHelpers.GetTargetApiConnectionDetails(),
                target,
                runSummaryCollector: summaryCollector,
                metadataCollector: metadataCollector,
                publishRunStateStore: store,
                pageCheckpointCoordinator: coordinator);

            bool failed = false;

            try
            {
                await changeProcessor.ProcessChangesAsync(TestHelpers.CreateChangeProcessorConfiguration(options), CancellationToken.None);
            }
            catch (Exception)
            {
                // A run that loses documents ends by throwing; the state it recorded is written from the
                // finally, which is exactly what makes it resumable
                failed = true;
            }

            var summary = summaryCollector.GetSummary();

            string described = "sourceReadErrors=" + summary.SourceReadErrorCount + "; "
                + string.Join(" | ", summary.Resources.Select(
                    r => r.Stage + " " + r.ResourcePath + " attempted=" + r.AttemptedItemCount + " published=" + r.PublishedItemCount
                        + " failed=" + r.FailedItemCount + " skipped=" + r.SkippedItemCount
                        + " unresolved=" + r.UnresolvedItemCount));

            return new RunOutcome(failed, pageTokens.ToArray(), partitionsNumbers.ToArray(), countWindows.ToArray(), described);
        }

        /// <summary>
        /// Stands in for the run state file, so that the second run reads what the first one left behind.
        /// </summary>
        private sealed class InMemoryRunStateStore : IPublishRunStateStore
        {
            public PublishRunState State { get; private set; }

            public string Location => "(in memory)";

            public Task<PublishRunState> TryLoadAsync(CancellationToken cancellationToken) => Task.FromResult(State);

            public Task<bool> SaveAsync(PublishRunState state, CancellationToken cancellationToken)
            {
                State = state;

                return Task.FromResult(true);
            }

            public Task DeleteAsync(CancellationToken cancellationToken)
            {
                State = null;

                return Task.CompletedTask;
            }
        }
    }
}
