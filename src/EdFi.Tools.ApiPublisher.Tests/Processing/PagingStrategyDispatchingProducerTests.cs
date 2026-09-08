// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Counting;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.MessageProducers;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Paging;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing.Blocks;
using EdFi.Tools.ApiPublisher.Core.Processing.Handlers;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using NUnit.Framework;
using Shouldly;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// Verifies that the dispatching producer (APIPUB-139) routes each resource to the offset-family producer or
    /// the cursor producer per the resolved strategy, and falls back to offset when the partitions request fails.
    /// </summary>
    [TestFixture]
    public class PagingStrategyDispatchingProducerTests
    {
        private const string PartitionsPath = "/data/v3/ed-fi/students/partitions";

        private static readonly StreamResourcePageMessage<object>[] _offsetMessages =
        {
            new() { ResourceUrl = "/ed-fi/students", Offset = 0, Limit = 500 },
        };

        private static (PagingStrategyDispatchingStreamResourcePageMessageProducer dispatcher, IStreamResourcePageMessageProducer offsetProducer, IFakeHttpRequestHandler fake)
            Create(SourcePagingStrategy strategy, Func<HttpResponseMessage> partitionsResponse)
        {
            var resolver = A.Fake<ISourcePagingStrategyResolver>();
            A.CallTo(() => resolver.ResolveAsync(A<string>.Ignored, A<Options>.Ignored)).Returns(strategy);

            var offsetProducer = A.Fake<IStreamResourcePageMessageProducer>();
            A.CallTo(() => offsetProducer.ProduceMessagesAsync(
                    A<StreamResourceMessage>.Ignored, A<Options>.Ignored, A<ITargetBlock<ErrorItemMessage>>.Ignored,
                    A<Func<StreamResourcePageMessage<object>, TextReader, Action<int>, IEnumerable<object>>>.Ignored, A<CancellationToken>.Ignored))
                .Returns(_offsetMessages);

            var fake = TestHelpers.GetFakeBaselineSourceApiRequestHandler().ResourceCount(responseTotalCountHeader: 10);
            A.CallTo(() => fake.Get(A<string>.Ignored, A<HttpRequestMessage>.That.Matches(msg => msg.RequestUri.LocalPath == PartitionsPath)))
                .ReturnsLazily(partitionsResponse);

            EdFiApiClient ClientFactory() =>
                new EdFiApiClient("TestSource", TestHelpers.GetSourceApiConnectionDetails(), 27, true,
                    httpClientHandler: new HttpClientHandlerFakeBridge(fake));

            var clientProvider = new EdFiApiClientProvider(new Lazy<EdFiApiClient>(ClientFactory));
            var cursorProducer = new EdFiApiCursorPagingStreamResourcePageMessageProducer(clientProvider, new EdFiApiSourceTotalCountProvider(clientProvider));

            return (new PagingStrategyDispatchingStreamResourcePageMessageProducer(resolver, offsetProducer, cursorProducer), offsetProducer, fake);
        }

        private static StreamResourceMessage Message() =>
            new() { ResourceUrl = "/ed-fi/students", PageSize = 500, CancellationSource = new CancellationTokenSource() };

        [Test]
        public async Task Offset_strategy_should_use_the_offset_producer_and_never_request_partitions()
        {
            var (dispatcher, _, fake) = Create(SourcePagingStrategy.Offset, () => FakeResponse.OK(new { pageTokens = new[] { "t1" } }));

            var messages = await dispatcher.ProduceMessagesAsync<object>(Message(), TestHelpers.GetOptions(), new BufferBlock<ErrorItemMessage>(), null, CancellationToken.None);

            messages.ShouldBe(_offsetMessages);
            A.CallTo(() => fake.Get(A<string>.Ignored, A<HttpRequestMessage>.That.Matches(msg => msg.RequestUri.LocalPath == PartitionsPath))).MustNotHaveHappened();
        }

        [Test]
        public async Task Cursor_strategy_should_use_the_cursor_producer()
        {
            var (dispatcher, offsetProducer, _) = Create(SourcePagingStrategy.Cursor, () => FakeResponse.OK(new { pageTokens = new[] { "t1", "t2" } }));

            var messages = (await dispatcher.ProduceMessagesAsync<object>(Message(), TestHelpers.GetOptions(), new BufferBlock<ErrorItemMessage>(), null, CancellationToken.None)).ToArray();

            messages.Select(m => m.PageToken).ShouldBe(new[] { "t1", "t2" });
            A.CallTo(offsetProducer).MustNotHaveHappened();
        }

        [Test]
        public async Task Cursor_strategy_should_fall_back_to_the_offset_producer_when_partitions_fail()
        {
            var (dispatcher, _, _) = Create(SourcePagingStrategy.Cursor, FakeResponse.NotFound);

            var messages = await dispatcher.ProduceMessagesAsync<object>(Message(), TestHelpers.GetOptions(), new BufferBlock<ErrorItemMessage>(), null, CancellationToken.None);

            messages.ShouldBe(_offsetMessages);
        }
    }
}
