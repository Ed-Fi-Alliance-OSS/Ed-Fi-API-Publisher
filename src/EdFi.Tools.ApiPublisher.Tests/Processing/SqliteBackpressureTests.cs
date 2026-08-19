// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Target.Blocks;
using EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Target.Messages;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing.Blocks;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Shouldly;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// Verifies that the SQLite target processing block participates in APIPUB-112 backpressure: it is the
    /// single-threaded, slowest consumer in the codebase, so leaving it unbounded would let it accept every
    /// offered message instantly and continuously free the upstream page bound, defeating the fix on that path.
    /// </summary>
    [TestFixture]
    public class SqliteBackpressureTests
    {
        private const string Students = "/ed-fi/students";

        [TestCase(100, true)]
        [TestCase(-1, false)]
        public async Task Sqlite_processing_block_should_bound_in_page_messages_and_write_all_accepted_items(
            int configuredCapacity,
            bool expectDecline)
        {
            TestHelpers.InitializeLogging();

            // Every message this block receives carries a whole page of items (see CreateProcessDataMessages),
            // so its bound must be denominated in pages: a 100-item capacity at page size 25 admits exactly
            // 4 page messages -- applying the item capacity directly would admit 100 whole pages (~25x).
            const int PageSize = 25;
            const int ExpectedPageCapacity = 4;
            const int ExtraOffers = 12;

            using var connectionGate = new ManualResetEventSlim(false);

            // A shared-cache in-memory database that survives across the factory's short-lived connections,
            // held alive by this connection for the duration of the test
            string connectionString = $"Data Source=SqliteBP{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            await using var keepAlive = new SqliteConnection(connectionString);
            keepAlive.Open();

            try
            {
                var options = TestHelpers.GetOptions();
                options.MaxDegreeOfParallelismForPostResourceItem = 1;
                options.StreamingPageSize = PageSize;
                options.ProcessingBlockBoundedCapacity = configuredCapacity;

                var factory = new UpsertProcessingBlocksFactory(
                    () =>
                    {
                        // Held closed so the first message parks inside the block delegate, keeping the buffer full
                        connectionGate.Wait(TimeSpan.FromSeconds(30));

                        return new SqliteConnection(connectionString);
                    });

                var createBlocksRequest = new CreateBlocksRequest(
                    options,
                    Array.Empty<AuthorizationFailureHandling>(),
                    new BufferBlock<ErrorItemMessage>(),
                    javaScriptModuleFactory: null);

                var (inputBlock, outputBlock) = factory.CreateProcessingBlocks(createBlocksRequest);
                outputBlock.LinkTo(DataflowBlock.NullTarget<ErrorItemMessage>());

                // Fill the block up to the page-denominated bound (accepted in both modes)
                int accepted = 0;

                for (int i = 0; i < ExpectedPageCapacity; i++)
                {
                    (await inputBlock.SendAsync(CreateMessage()).WaitAsync(TimeSpan.FromSeconds(30))).ShouldBeTrue();
                    accepted++;
                }

                // With the consumer stalled and the buffer full, a bounded block must decline every further
                // offer, while an unbounded block (-1, the rollback configuration) must accept all of them
                bool declined = false;

                for (int i = 0; i < ExtraOffers; i++)
                {
                    if (inputBlock.Post(CreateMessage()))
                    {
                        accepted++;
                    }
                    else
                    {
                        declined = true;
                    }
                }

                declined.ShouldBe(expectDecline);

                accepted.ShouldBe(expectDecline
                    ? ExpectedPageCapacity
                    : ExpectedPageCapacity + ExtraOffers);

                // Release the consumer and drain; every accepted message must be written (no loss, no errors)
                connectionGate.Set();
                inputBlock.Complete();
                await outputBlock.Completion.WaitAsync(TimeSpan.FromSeconds(30));

                var countCommand = keepAlive.CreateCommand();
                countCommand.CommandText = "SELECT COUNT(*) FROM ed_fi__students";
                Convert.ToInt32(countCommand.ExecuteScalar()).ShouldBe(accepted);
            }
            finally
            {
                connectionGate.Set();
            }
        }

        private static UpsertsJsonMessage CreateMessage()
        {
            return new UpsertsJsonMessage
            {
                ResourceUrl = Students,
                Json = "{\"someProperty\":\"someValue\"}",
            };
        }
    }
}
