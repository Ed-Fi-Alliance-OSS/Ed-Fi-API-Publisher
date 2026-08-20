// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Connections.Api.DependencyResolution;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Blocks;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Messages;
using EdFi.Tools.ApiPublisher.Core.Capabilities;
using EdFi.Tools.ApiPublisher.Core.Helpers;
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

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    [TestFixture]
    public class JsonHelpersTests
    {
        [TestCase("[]", 0)]
        [TestCase("[1, 2, 3]", 3)]
        [TestCase(@"[""a"", null, true, 1.5]", 4)]
        [TestCase(@"[{""a"":1},{""b"":{""c"":[1,2,3]}}]", 2)]
        [TestCase(@"[[1,2],[3],[]]", 3)]
        [TestCase("[new Date(1)]", 1)]
        [TestCase("[new Date(1), new Date(2, 3)]", 2)]
        public void CountTopLevelArrayItems_should_count_only_top_level_elements(string json, int expectedCount)
        {
            JsonHelpers.CountTopLevelArrayItems(json).ShouldBe(expectedCount);
        }

        [TestCase("{}")]
        [TestCase(@"{""a"": [1,2,3]}")]
        [TestCase("not json")]
        public void CountTopLevelArrayItems_should_throw_for_input_that_is_not_a_json_array(string json)
        {
            Should.Throw<JsonReaderException>(() => JsonHelpers.CountTopLevelArrayItems(json));
        }

        [TestCase("[] {}")]
        [TestCase("[1,2] 3")]
        [TestCase("[]]")]
        [TestCase("[]garbage")]
        public void CountTopLevelArrayItems_should_throw_for_trailing_content_after_the_array(string json)
        {
            Should.Throw<JsonReaderException>(() => JsonHelpers.CountTopLevelArrayItems(json));
        }

        [TestCase("[] ", 0)]
        [TestCase("[1, 2]\r\n", 2)]
        public void CountTopLevelArrayItems_should_tolerate_trailing_whitespace(string json, int expectedCount)
        {
            JsonHelpers.CountTopLevelArrayItems(json).ShouldBe(expectedCount);
        }

        [TestCase("[]", 0)]
        [TestCase("[1, 2, 3]", 3)]
        [TestCase(@"[""a"", null, true, 1.5]", 4)]
        [TestCase(@"[{""a"":1},{""b"":{""c"":[1,2,3]}}]", 2)]
        [TestCase(@"[[1,2],[3],[]]", 3)]
        [TestCase("[new Date(1)]", 1)]
        [TestCase("[new Date(1), new Date(2, 3)]", 2)]
        public void EnumerateTopLevelArrayItems_should_yield_each_top_level_element_and_report_count(string json, int expectedCount)
        {
            int? reportedCount = null;

            using var reader = new StringReader(json);

            var items = JsonHelpers.EnumerateTopLevelArrayItems(reader, count => reportedCount = count).ToArray();

            items.Length.ShouldBe(expectedCount);
            reportedCount.ShouldBe(expectedCount);

            // The yielded tokens must be faithful to the source elements
            var expectedItems = JArray.Parse(json, JsonHelpers.NoLineInfoLoadSettings);
            items.Select(i => i.ToString(Formatting.None)).ShouldBe(expectedItems.Select(i => i.ToString(Formatting.None)));
        }

        [TestCase("{}")]
        [TestCase(@"{""a"": [1,2,3]}")]
        [TestCase("not json")]
        public void EnumerateTopLevelArrayItems_should_throw_for_input_that_is_not_a_json_array(string json)
        {
            using var reader = new StringReader(json);

            Should.Throw<JsonReaderException>(() => JsonHelpers.EnumerateTopLevelArrayItems(reader).ToArray());
        }

        [TestCase("[] {}")]
        [TestCase("[1,2] 3")]
        [TestCase("[]]")]
        [TestCase("[]garbage")]
        public void EnumerateTopLevelArrayItems_should_throw_for_trailing_content_after_the_array(string json)
        {
            using var reader = new StringReader(json);

            Should.Throw<JsonReaderException>(() => JsonHelpers.EnumerateTopLevelArrayItems(reader).ToArray());
        }

        [TestCase("[] ", 0)]
        [TestCase("[1, 2]\r\n", 2)]
        public void EnumerateTopLevelArrayItems_should_tolerate_trailing_whitespace(string json, int expectedCount)
        {
            using var reader = new StringReader(json);

            JsonHelpers.EnumerateTopLevelArrayItems(reader).Count().ShouldBe(expectedCount);
        }

        [Test]
        public void EnumerateTopLevelArrayItems_should_not_report_count_when_enumeration_stops_early()
        {
            int? reportedCount = null;

            using var reader = new StringReader("[1, 2, 3]");

            var firstItem = JsonHelpers.EnumerateTopLevelArrayItems(reader, count => reportedCount = count)
                .Take(1)
                .ToArray();

            firstItem.Length.ShouldBe(1);
            reportedCount.ShouldBeNull();
        }

        [Test]
        public void EnumerateTopLevelArrayItems_should_not_attach_line_info_to_yielded_tokens()
        {
            using var reader = new StringReader(@"[{""id"":""abc123"",""nested"":{""property"":""value""}}]");

            var items = JsonHelpers.EnumerateTopLevelArrayItems(reader).ToArray();

            ((IJsonLineInfo)items[0]).HasLineInfo().ShouldBeFalse();
            ((IJsonLineInfo)items[0]["nested"]).HasLineInfo().ShouldBeFalse();
        }

        [Test]
        public void EnumerateTopLevelArrayItems_should_not_close_the_supplied_reader()
        {
            using var reader = new StringReader("[1]");

            JsonHelpers.EnumerateTopLevelArrayItems(reader).ToArray();

            // A closed StringReader throws ObjectDisposedException from Read()
            Should.NotThrow(() => reader.Read());
        }

        [Test]
        public void CreateProcessDataMessages_should_not_attach_line_info_to_parsed_items()
        {
            TestHelpers.InitializeLogging();

            var fakeTargetRequestHandler = TestHelpers.GetFakeBaselineTargetApiRequestHandler();

            EdFiApiClient TargetApiClientFactory() =>
                new EdFiApiClient(
                    "TestTarget",
                    TestHelpers.GetTargetApiConnectionDetails(),
                    bearerTokenRefreshMinutes: 27,
                    ignoreSslErrors: true,
                    httpClientHandler: new HttpClientHandlerFakeBridge(fakeTargetRequestHandler));

            var factory = new PostResourceProcessingBlocksFactory(
                A.Fake<INodeJSService>(),
                new EdFiApiClientProvider(new Lazy<EdFiApiClient>(TargetApiClientFactory)),
                TestHelpers.GetSourceApiConnectionDetails(),
                A.Fake<ISourceCapabilities>(),
                A.Fake<ISourceResourceItemProvider>());

            var pageMessage = new StreamResourcePageMessage<PostItemMessage>
            {
                ResourceUrl = "/ed-fi/students",
                CancellationSource = new CancellationTokenSource(),
            };

            const string Json = @"[{""id"":""abc123"",""nested"":{""property"":""value""}}]";

            using var jsonReader = new StringReader(Json);

            var itemMessages = factory.CreateProcessDataMessages(pageMessage, jsonReader, null).ToArray();

            itemMessages.Length.ShouldBe(1);

            ((IJsonLineInfo)itemMessages[0].Item).HasLineInfo().ShouldBeFalse();
            ((IJsonLineInfo)itemMessages[0].Item["nested"]).HasLineInfo().ShouldBeFalse();
        }
    }
}
