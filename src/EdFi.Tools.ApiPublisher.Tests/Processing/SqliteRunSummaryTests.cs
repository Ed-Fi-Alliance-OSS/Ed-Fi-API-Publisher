// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Target.Blocks;
using EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Target.Messages;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Metadata;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Blocks;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Shouldly;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// The SQLite target writes a whole page per message, unlike every API target, which writes a document per
    /// message. The run summary counts documents, so the page has to say how many it carries; counting messages
    /// reported a page of 500 documents as one (see APIPUB-120).
    /// </summary>
    [TestFixture]
    public class SqliteRunSummaryTests
    {
        private const string Students = "/ed-fi/students";

        [Test]
        public async Task A_written_page_is_published_as_the_documents_it_carried()
        {
            TestHelpers.InitializeLogging();

            const int DocumentsInPage = 3;

            string connectionString = $"Data Source=SqliteSummary{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            await using var keepAlive = new SqliteConnection(connectionString);
            keepAlive.Open();

            var metadataCollector = new PublishingOperationMetadataCollector();
            var runSummaryCollector = new RunSummaryCollector(metadataCollector);

            var factory = new UpsertProcessingBlocksFactory(
                () => new SqliteConnection(connectionString),
                runSummaryCollector);

            var (inputBlock, outputBlock) = factory.CreateProcessingBlocks(
                new CreateBlocksRequest(
                    TestHelpers.GetOptions(),
                    Array.Empty<AuthorizationFailureHandling>(),
                    new BufferBlock<ErrorItemMessage>(),
                    javaScriptModuleFactory: null));

            outputBlock.LinkTo(DataflowBlock.NullTarget<ErrorItemMessage>());

            await inputBlock.SendAsync(
                new UpsertsJsonMessage
                {
                    ResourceUrl = Students,
                    Json = @"[{""id"":""1""}, {""id"":""2""}, {""id"":""3""}]",
                    ItemCount = DocumentsInPage,
                });

            inputBlock.Complete();
            await outputBlock.Completion.WaitAsync(TimeSpan.FromSeconds(30));

            var resource = runSummaryCollector.GetSummary().Resources
                .Single(r => r.Stage == PublishingStage.Upserts && r.ResourcePath == Students);

            resource.PublishedItemCount.ShouldBe(DocumentsInPage);
            resource.FailedItemCount.ShouldBe(0);
        }

        [Test]
        public void A_page_that_could_not_be_written_fails_as_the_documents_it_carried()
        {
            // The error stands for the whole page, so counting it as one document would let a run lose a page
            // of documents inside a tolerance meant for one
            var metadataCollector = new PublishingOperationMetadataCollector();
            var runSummaryCollector = new RunSummaryCollector(metadataCollector);

            runSummaryCollector.AddAttemptedItems(Students, 3);

            runSummaryCollector.AddError(
                PublishingStage.Upserts,
                new ErrorItemMessage
                {
                    ResourceUrl = Students,
                    ItemCount = 3,
                });

            var resource = runSummaryCollector.GetSummary().Resources.Single();

            resource.FailedItemCount.ShouldBe(3);
            resource.UnresolvedItemCount.ShouldBe(0);
        }
    }
}
