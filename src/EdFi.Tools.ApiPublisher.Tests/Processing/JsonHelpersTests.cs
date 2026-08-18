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
using NUnit.Framework;
using Shouldly;
using System;
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

            var itemMessages = factory.CreateProcessDataMessages(pageMessage, Json).ToArray();

            itemMessages.Length.ShouldBe(1);

            ((IJsonLineInfo)itemMessages[0].Item).HasLineInfo().ShouldBeFalse();
            ((IJsonLineInfo)itemMessages[0].Item["nested"]).HasLineInfo().ShouldBeFalse();
        }
    }
}
