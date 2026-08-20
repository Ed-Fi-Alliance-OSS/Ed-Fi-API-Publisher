// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Connections.Api.DependencyResolution;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Blocks;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Messages;
using EdFi.Tools.ApiPublisher.Core.Capabilities;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using Jering.Javascript.NodeJS;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Shouldly;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using SqliteMessages = EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Target.Messages;
using SqliteUpsertFactory = EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Target.Blocks.UpsertProcessingBlocksFactory;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// Verifies the reader-based <c>CreateProcessDataMessages</c> seam introduced by APIPUB-134: single-pass
    /// streamed item splitting with the top-level element count reported through the callback (which cannot
    /// be inferred from the number of messages produced).
    /// </summary>
    [TestFixture]
    public class CreateProcessDataMessagesTests
    {
        private static StreamResourcePageMessage<TProcessDataMessage> CreatePageMessage<TProcessDataMessage>(string resourceUrl)
        {
            return new StreamResourcePageMessage<TProcessDataMessage>
            {
                ResourceUrl = resourceUrl,
                CancellationSource = new CancellationTokenSource(),
            };
        }

        private static PostResourceProcessingBlocksFactory CreatePostFactory()
        {
            // The factory's constructor dereferences the target API client, so a real provider over a
            // fake-backed client is required (matching JsonHelpersTests)
            var fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();

            EdFiApiClient TargetApiClientFactory() =>
                new EdFiApiClient(
                    "TestTarget",
                    TestHelpers.GetTargetApiConnectionDetails(),
                    bearerTokenRefreshMinutes: 27,
                    ignoreSslErrors: true,
                    httpClientHandler: new HttpClientHandlerFakeBridge(fakeTargetRequestHandler));

            return new PostResourceProcessingBlocksFactory(
                A.Fake<INodeJSService>(),
                new EdFiApiClientProvider(new Lazy<EdFiApiClient>(TargetApiClientFactory)),
                A.Fake<ISourceConnectionDetails>(),
                A.Fake<ISourceCapabilities>(),
                A.Fake<ISourceResourceItemProvider>());
        }

        [Test]
        public void Post_factory_should_skip_non_object_elements_but_still_report_the_full_top_level_count()
        {
            TestHelpers.InitializeLogging();

            var factory = CreatePostFactory();
            var pageMessage = CreatePageMessage<PostItemMessage>("/ed-fi/students");

            int? reportedCount = null;

            using var jsonReader = new StringReader(@"[{""id"":""1""}, 42, {""id"":""2""}]");

            var itemMessages = factory.CreateProcessDataMessages(pageMessage, jsonReader, count => reportedCount = count)
                .ToArray();

            itemMessages.Length.ShouldBe(2);
            itemMessages.Select(m => m.Item["id"]!.Value<string>()).ShouldBe(new[] { "1", "2" });
            itemMessages.ShouldAllBe(m => m.ResourceUrl == "/ed-fi/students");

            // The count reflects top-level array elements, not messages produced
            reportedCount.ShouldBe(3);
        }

        [Test]
        public void Post_factory_should_tolerate_a_null_count_callback()
        {
            TestHelpers.InitializeLogging();

            var factory = CreatePostFactory();
            var pageMessage = CreatePageMessage<PostItemMessage>("/ed-fi/students");

            using var jsonReader = new StringReader(@"[{""id"":""1""}]");

            factory.CreateProcessDataMessages(pageMessage, jsonReader, null).Count().ShouldBe(1);
        }

        [Test]
        public void Delete_factory_should_produce_messages_and_report_count_for_pages_with_key_values()
        {
            TestHelpers.InitializeLogging();

            var factory = new DeleteResourceProcessingBlocksFactory(A.Fake<ITargetEdFiApiClientProvider>());
            var pageMessage = CreatePageMessage<GetItemForDeletionMessage>("/ed-fi/students/deletes");

            int? reportedCount = null;

            using var jsonReader = new StringReader(
                @"[{""id"":""a1"",""keyValues"":{""studentUniqueId"":""1""}},{""id"":""a2"",""keyValues"":{""studentUniqueId"":""2""}}]");

            var itemMessages = factory.CreateProcessDataMessages(pageMessage, jsonReader, count => reportedCount = count)
                .ToArray();

            itemMessages.Length.ShouldBe(2);
            itemMessages.ShouldAllBe(m => m.ResourceUrl == "/ed-fi/students");
            reportedCount.ShouldBe(2);
        }

        [Test]
        public void Delete_factory_should_cancel_without_reporting_count_when_key_values_are_missing()
        {
            TestHelpers.InitializeLogging();

            var factory = new DeleteResourceProcessingBlocksFactory(A.Fake<ITargetEdFiApiClientProvider>());
            var pageMessage = CreatePageMessage<GetItemForDeletionMessage>("/ed-fi/students/deletes");

            int? reportedCount = null;

            using var jsonReader = new StringReader(@"[{""id"":""a1""},{""id"":""a2""}]");

            var itemMessages = factory.CreateProcessDataMessages(pageMessage, jsonReader, count => reportedCount = count)
                .ToArray();

            itemMessages.ShouldBeEmpty();
            pageMessage.CancellationSource.IsCancellationRequested.ShouldBeTrue();

            // Enumeration stopped early, so no count is reported (and the handler treats that as "do not continue paging")
            reportedCount.ShouldBeNull();
        }

        [Test]
        public void ChangeKey_factory_should_produce_messages_and_report_count_for_pages_with_old_key_values()
        {
            TestHelpers.InitializeLogging();

            var factory = new ChangeResourceKeyProcessingBlocksFactory(A.Fake<ITargetEdFiApiClientProvider>());
            var pageMessage = CreatePageMessage<GetItemForKeyChangeMessage>("/ed-fi/students/keyChanges");

            int? reportedCount = null;

            using var jsonReader = new StringReader(
                @"[{""id"":""a1"",""oldKeyValues"":{""studentUniqueId"":""1""},""newKeyValues"":{""studentUniqueId"":""2""}}]");

            var itemMessages = factory.CreateProcessDataMessages(pageMessage, jsonReader, count => reportedCount = count)
                .ToArray();

            itemMessages.Length.ShouldBe(1);
            itemMessages[0].ResourceUrl.ShouldBe("/ed-fi/students");
            reportedCount.ShouldBe(1);
        }

        [Test]
        public void ChangeKey_factory_should_cancel_without_reporting_count_when_old_key_values_are_missing()
        {
            TestHelpers.InitializeLogging();

            var factory = new ChangeResourceKeyProcessingBlocksFactory(A.Fake<ITargetEdFiApiClientProvider>());
            var pageMessage = CreatePageMessage<GetItemForKeyChangeMessage>("/ed-fi/students/keyChanges");

            int? reportedCount = null;

            using var jsonReader = new StringReader(@"[{""id"":""a1""}]");

            var itemMessages = factory.CreateProcessDataMessages(pageMessage, jsonReader, count => reportedCount = count)
                .ToArray();

            itemMessages.ShouldBeEmpty();
            pageMessage.CancellationSource.IsCancellationRequested.ShouldBeTrue();
            reportedCount.ShouldBeNull();
        }

        [Test]
        public void Sqlite_factory_should_buffer_the_whole_page_and_report_count_when_requested()
        {
            TestHelpers.InitializeLogging();

            var factory = new SqliteUpsertFactory(() => null);
            var pageMessage = CreatePageMessage<SqliteMessages.UpsertsJsonMessage>("/ed-fi/students");

            int? reportedCount = null;

            const string Json = @"[{""id"":""1""}, {""id"":""2""}, {""id"":""3""}]";

            using var jsonReader = new StringReader(Json);

            var pageMessages = factory.CreateProcessDataMessages(pageMessage, jsonReader, count => reportedCount = count)
                .ToArray();

            // Documented exemption: one message carrying the whole page string
            pageMessages.Length.ShouldBe(1);
            pageMessages[0].Json.ShouldBe(Json);
            reportedCount.ShouldBe(3);
        }

        [Test]
        public void Sqlite_factory_should_skip_the_counting_pass_when_no_callback_is_supplied()
        {
            TestHelpers.InitializeLogging();

            var factory = new SqliteUpsertFactory(() => null);
            var pageMessage = CreatePageMessage<SqliteMessages.UpsertsJsonMessage>("/ed-fi/students");

            // Non-array content would make the counting pass throw, proving it is skipped for null callbacks
            const string Json = @"{""not"":""an array""}";

            using var jsonReader = new StringReader(Json);

            var pageMessages = factory.CreateProcessDataMessages(pageMessage, jsonReader, null).ToArray();

            pageMessages.Length.ShouldBe(1);
            pageMessages[0].Json.ShouldBe(Json);
        }

        [Test]
        public void Sqlite_factory_should_throw_for_non_array_content_when_a_count_is_requested()
        {
            TestHelpers.InitializeLogging();

            var factory = new SqliteUpsertFactory(() => null);
            var pageMessage = CreatePageMessage<SqliteMessages.UpsertsJsonMessage>("/ed-fi/students");

            using var jsonReader = new StringReader(@"{""not"":""an array""}");

            Should.Throw<JsonReaderException>(
                () => factory.CreateProcessDataMessages(pageMessage, jsonReader, count => { }).ToArray());
        }
    }
}
