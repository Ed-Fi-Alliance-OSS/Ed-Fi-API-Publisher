// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Metadata;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using NUnit.Framework;
using Shouldly;
using System.Linq;
using System.Net.Http;

namespace EdFi.Tools.ApiPublisher.Tests.Metadata
{
    /// <summary>
    /// The run summary is only as good as the bookkeeping behind it: which errors count against a resource,
    /// which stage a resource belongs to, and whether two spellings of the same path land on one row
    /// (see APIPUB-120).
    /// </summary>
    [TestFixture]
    public class RunSummaryCollectorTests
    {
        private const string Students = "/ed-fi/students";

        [Test]
        public void A_document_the_target_rejected_is_counted_against_its_resource()
        {
            var collector = CreateCollector();

            collector.AddAttemptedItems(Students, 3);
            collector.AddError(
                PublishingStage.Upserts,
                new ErrorItemMessage
                {
                    Method = HttpMethod.Post.ToString(),
                    ResourceUrl = Students,
                    Id = "d0ed1d0b",
                });

            var resource = collector.GetSummary().Resources.Single(r => r.AttemptedItemCount > 0);

            resource.FailedItemCount.ShouldBe(1);
            resource.PublishedItemCount.ShouldBe(2);
            collector.GetSummary().SourceReadErrorCount.ShouldBe(0);
        }

        [Test]
        public void A_source_that_could_not_be_read_is_reported_apart_from_the_resource_counts()
        {
            var collector = CreateCollector();

            collector.AddAttemptedItems(Students, 3);
            collector.AddError(
                PublishingStage.Upserts,
                new ErrorItemMessage
                {
                    IsSourceReadError = true,
                    Method = HttpMethod.Get.ToString(),
                    ResourceUrl = $"data/v3{Students}",
                });

            var summary = collector.GetSummary();

            summary.SourceReadErrorCount.ShouldBe(1);
            summary.Resources.Single(r => r.AttemptedItemCount > 0).FailedItemCount.ShouldBe(0);
        }

        [Test]
        public void A_source_page_failure_that_carries_an_id_is_still_a_source_read_error()
        {
            // The SQLite source connector identifies a failed page by its page id, so classifying by the
            // absence of an id charged a whole lost page to the resource as a single rejected document
            var collector = CreateCollector();

            collector.AddError(
                PublishingStage.Upserts,
                new ErrorItemMessage
                {
                    IsSourceReadError = true,
                    ResourceUrl = Students,
                    Id = "17",
                });

            var summary = collector.GetSummary();

            summary.SourceReadErrorCount.ShouldBe(1);
            summary.Resources.ShouldAllBe(resource => resource.FailedItemCount == 0);
        }

        [Test]
        public void A_target_lookup_for_one_document_counts_against_the_resource_even_without_an_id()
        {
            // A delete whose source item carried no id still fails one document, not a page
            var collector = CreateCollector();

            collector.AddAttemptedItems($"{Students}{EdFiApiConstants.DeletesPathSuffix}", 2);
            collector.AddError(
                PublishingStage.Deletes,
                new ErrorItemMessage
                {
                    Method = HttpMethod.Get.ToString(),
                    ResourceUrl = $"{Students}?studentUniqueId=123",
                    Id = null,
                });

            var summary = collector.GetSummary();

            summary.SourceReadErrorCount.ShouldBe(0);

            var deletes = summary.Resources.Single(resource => resource.Stage == PublishingStage.Deletes);

            deletes.ResourcePath.ShouldBe(Students);
            deletes.FailedItemCount.ShouldBe(1);
            deletes.PublishedItemCount.ShouldBe(1);
        }

        [Test]
        public void A_stage_suffix_and_a_query_string_collapse_onto_the_resource_row()
        {
            var collector = CreateCollector();

            collector.AddAttemptedItems($"{Students}{EdFiApiConstants.KeyChangesPathSuffix}?offset=0&limit=100", 5);
            collector.AddError(
                PublishingStage.KeyChanges,
                new ErrorItemMessage
                {
                    Method = HttpMethod.Put.ToString(),
                    ResourceUrl = Students,
                    Id = "d0ed1d0b",
                });

            var keyChanges = collector.GetSummary().Resources
                .Single(resource => resource.Stage == PublishingStage.KeyChanges);

            keyChanges.Stage.ShouldBe(PublishingStage.KeyChanges);
            keyChanges.ResourcePath.ShouldBe(Students);
            keyChanges.AttemptedItemCount.ShouldBe(5);
            keyChanges.FailedItemCount.ShouldBe(1);
        }

        [Test]
        public void The_same_resource_in_a_different_case_is_one_row()
        {
            // The dependency graph, the include lists and the item counts are all treated as case-insensitive
            // elsewhere in the pipeline, so the summary cannot be the one place that is not
            var collector = CreateCollector();

            collector.AddAttemptedItems(Students, 2);
            collector.AddError(
                PublishingStage.Upserts,
                new ErrorItemMessage
                {
                    Method = HttpMethod.Post.ToString(),
                    ResourceUrl = "/ed-fi/Students",
                    Id = "d0ed1d0b",
                });

            var resource = collector.GetSummary().Resources.ShouldHaveSingleItem();

            resource.AttemptedItemCount.ShouldBe(2);
            resource.FailedItemCount.ShouldBe(1);
        }

        [Test]
        public void An_item_count_the_source_could_not_report_is_surfaced_as_unknown()
        {
            var metadataCollector = new PublishingOperationMetadataCollector();
            metadataCollector.SetResourceItemCount(Students, -1);

            var collector = new RunSummaryCollector(metadataCollector);
            collector.AddAttemptedItems(Students, 1);

            collector.GetSummary().Resources.Single().ExpectedItemCount.ShouldBeNull();
        }

        [Test]
        public void Documents_abandoned_by_configuration_are_counted_as_skipped_with_their_reason()
        {
            var collector = CreateCollector();

            collector.AddAttemptedItems(Students, 4);
            collector.AddSkippedItems(
                PublishingStage.Upserts,
                Students,
                3,
                SkipReasons.ResourceIgnoredAfterAuthorizationFailure);

            var summary = collector.GetSummary();
            var resource = summary.Resources.Single();

            resource.SkippedItemCount.ShouldBe(3);
            resource.PublishedItemCount.ShouldBe(1);
            summary.SkipReasons.ShouldContain(SkipReasons.ResourceIgnoredAfterAuthorizationFailure);
        }

        private static RunSummaryCollector CreateCollector()
        {
            var metadataCollector = new PublishingOperationMetadataCollector();
            metadataCollector.SetResourceItemCount(Students, 3);

            return new RunSummaryCollector(metadataCollector);
        }
    }
}
