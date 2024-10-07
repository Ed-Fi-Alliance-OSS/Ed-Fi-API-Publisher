// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Counting;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.MessageProducers;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using NUnit.Framework;
using Shouldly;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    [TestFixture]
    public class ReversePagingTests
    {
        [TestFixture]
        public class When_producing_messages_with_reverse_paging
        {
            [TestFixture]
            public class Given_100_records_and_ChangeVersion_page_size_equal_40_and_pageSize_equal_to_40 : TestFixtureAsyncBase
            {
                private IFakeHttpRequestHandler _fakeSourceRequestHandler;
                private EdFiApiChangeVersionReversePagingStreamResourcePageMessageProducer _messageProducer;
                private Options _options;
                private int _pageSize = 40;

                protected override async Task ArrangeAsync()
                {
                    // -----------------------------------------------------------------
                    //                      Source Requests
                    // -----------------------------------------------------------------
                    var sourceResourceFaker = TestHelpers.GetGenericResourceFaker();

                    var suppliedSourceResources = sourceResourceFaker.Generate(100);

                    // Prepare the fake source API endpoint
                    _fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler()

                        // Test-specific mocks
                        .AvailableChangeVersions(1100)
                        .ResourceCount(responseTotalCountHeader: 100);

                    var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(
                        includeOnly: new[] { "schools" });

                    _options = TestHelpers.GetOptions();
                    _options.IncludeDescriptors = false; // Shorten test execution time
                    _options.UseChangeVersionPaging = true;
                    _options.UseReversePaging = true;
                    _options.StreamingPageSize = _pageSize;
                    _options.ChangeVersionPagingWindowSize = 40;

                    await Task.Yield();

                    EdFiApiClient SourceApiClientFactory() =>
                        new EdFiApiClient(
                            "TestSource",
                            sourceApiConnectionDetails,
                            bearerTokenRefreshMinutes: 27,
                            ignoreSslErrors: true,
                            httpClientHandler: new HttpClientHandlerFakeBridge(_fakeSourceRequestHandler));

                    var sourceEdFiApiClientProvider = new EdFiApiClientProvider(new Lazy<EdFiApiClient>(SourceApiClientFactory));

                    _messageProducer = new EdFiApiChangeVersionReversePagingStreamResourcePageMessageProducer(
                        new EdFiApiSourceTotalCountProvider(sourceEdFiApiClientProvider));
                }

                [TestCase("schools")]
                public async Task Should_produce_9_pages_with_3_change_query_windows(string resourceCollectionName)
                {
                    StreamResourceMessage msg = new StreamResourceMessage
                    {
                        ChangeWindow = new ChangeWindow()
                        {
                            MaxChangeVersion = 100,
                            MinChangeVersion = 0

                        },
                        ResourceUrl = $"{EdFiApiConstants.DataManagementApiSegment}{TestHelpers.AnyResourcePattern}",
                        PageSize = _pageSize
                    };

                    var pageMessages = await _messageProducer.ProduceMessagesAsync<string>(msg, _options, null, null, new CancellationToken());

                    pageMessages.ElementAt(0).ChangeWindow.ShouldBeEquivalentTo(new ChangeWindow() { MinChangeVersion = 0, MaxChangeVersion = 40 });
                    pageMessages.ElementAt(1).ChangeWindow.ShouldBeEquivalentTo(new ChangeWindow() { MinChangeVersion = 0, MaxChangeVersion = 40 });
                    pageMessages.ElementAt(2).ChangeWindow.ShouldBeEquivalentTo(new ChangeWindow() { MinChangeVersion = 0, MaxChangeVersion = 40 });
                    pageMessages.ElementAt(3).ChangeWindow.ShouldBeEquivalentTo(new ChangeWindow() { MinChangeVersion = 41, MaxChangeVersion = 80 });
                    pageMessages.ElementAt(4).ChangeWindow.ShouldBeEquivalentTo(new ChangeWindow() { MinChangeVersion = 41, MaxChangeVersion = 80 });
                    pageMessages.ElementAt(5).ChangeWindow.ShouldBeEquivalentTo(new ChangeWindow() { MinChangeVersion = 41, MaxChangeVersion = 80 });
                    pageMessages.ElementAt(6).ChangeWindow.ShouldBeEquivalentTo(new ChangeWindow() { MinChangeVersion = 81, MaxChangeVersion = 100 });
                    pageMessages.ElementAt(7).ChangeWindow.ShouldBeEquivalentTo(new ChangeWindow() { MinChangeVersion = 81, MaxChangeVersion = 100 });
                    pageMessages.ElementAt(8).ChangeWindow.ShouldBeEquivalentTo(new ChangeWindow() { MinChangeVersion = 81, MaxChangeVersion = 100 });

                    pageMessages.ElementAt(0).Offset.ShouldBe(60);
                    pageMessages.ElementAt(0).Limit.ShouldBe(_pageSize);

                    pageMessages.ElementAt(1).Offset.ShouldBe(20);
                    pageMessages.ElementAt(1).Limit.ShouldBe(_pageSize);

                    pageMessages.ElementAt(2).Offset.ShouldBe(0);
                    pageMessages.ElementAt(2).Limit.ShouldBe(20);

                    pageMessages.ElementAt(3).Offset.ShouldBe(60);
                    pageMessages.ElementAt(3).Limit.ShouldBe(_pageSize);

                    pageMessages.ElementAt(4).Offset.ShouldBe(20);
                    pageMessages.ElementAt(4).Limit.ShouldBe(_pageSize);

                    pageMessages.ElementAt(5).Offset.ShouldBe(0);
                    pageMessages.ElementAt(5).Limit.ShouldBe(20);

                    pageMessages.ElementAt(6).Offset.ShouldBe(60);
                    pageMessages.ElementAt(6).Limit.ShouldBe(_pageSize);

                    pageMessages.ElementAt(7).Offset.ShouldBe(20);
                    pageMessages.ElementAt(7).Limit.ShouldBe(_pageSize);

                    pageMessages.ElementAt(8).Offset.ShouldBe(0);
                    pageMessages.ElementAt(8).Limit.ShouldBe(20);
                }
            }

            [TestFixture]
            public class Given_100_records_and_ChangeVersion_page_size_equal_40_and_pageSize_equal_to_50 : TestFixtureAsyncBase
            {
                private IFakeHttpRequestHandler _fakeSourceRequestHandler;
                private EdFiApiChangeVersionReversePagingStreamResourcePageMessageProducer _messageProducer;
                private Options _options;
                private int _pageSize = 50;

                protected override async Task ArrangeAsync()
                {
                    // -----------------------------------------------------------------
                    //                      Source Requests
                    // -----------------------------------------------------------------
                    var sourceResourceFaker = TestHelpers.GetGenericResourceFaker();

                    var suppliedSourceResources = sourceResourceFaker.Generate(100);

                    // Prepare the fake source API endpoint
                    _fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler()

                        // Test-specific mocks
                        .AvailableChangeVersions(1100)
                        .ResourceCount(responseTotalCountHeader: 100);

                    var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(
                        includeOnly: new[] { "schools" });

                    _options = TestHelpers.GetOptions();
                    _options.IncludeDescriptors = false; // Shorten test execution time
                    _options.UseChangeVersionPaging = true;
                    _options.UseReversePaging = true;
                    _options.StreamingPageSize = _pageSize;
                    _options.ChangeVersionPagingWindowSize = 40;

                    await Task.Yield();

                    EdFiApiClient SourceApiClientFactory() =>
                        new EdFiApiClient(
                            "TestSource",
                            sourceApiConnectionDetails,
                            bearerTokenRefreshMinutes: 27,
                            ignoreSslErrors: true,
                            httpClientHandler: new HttpClientHandlerFakeBridge(_fakeSourceRequestHandler));


                    var sourceEdFiApiClientProvider = new EdFiApiClientProvider(new Lazy<EdFiApiClient>(SourceApiClientFactory));

                    _messageProducer = new EdFiApiChangeVersionReversePagingStreamResourcePageMessageProducer(
                        new EdFiApiSourceTotalCountProvider(sourceEdFiApiClientProvider));
                }

                [TestCase("schools")]
                public async Task Should_produce_6_messages_with_3_change_query_windows(string resourceCollectionName)
                {
                    StreamResourceMessage msg = new StreamResourceMessage
                    {
                        ChangeWindow = new ChangeWindow()
                        {
                            MaxChangeVersion = 100,
                            MinChangeVersion = 0

                        },
                        ResourceUrl = $"{EdFiApiConstants.DataManagementApiSegment}{TestHelpers.AnyResourcePattern}",
                        PageSize = _pageSize
                    };

                    var pageMessages = await _messageProducer.ProduceMessagesAsync<string>(msg, _options, null, null, new CancellationToken());

                    pageMessages.ElementAt(0).ChangeWindow.ShouldBeEquivalentTo(new ChangeWindow() { MinChangeVersion = 0, MaxChangeVersion = 40 });
                    pageMessages.ElementAt(1).ChangeWindow.ShouldBeEquivalentTo(new ChangeWindow() { MinChangeVersion = 0, MaxChangeVersion = 40 });
                    pageMessages.ElementAt(2).ChangeWindow.ShouldBeEquivalentTo(new ChangeWindow() { MinChangeVersion = 41, MaxChangeVersion = 80 });
                    pageMessages.ElementAt(3).ChangeWindow.ShouldBeEquivalentTo(new ChangeWindow() { MinChangeVersion = 41, MaxChangeVersion = 80 });
                    pageMessages.ElementAt(4).ChangeWindow.ShouldBeEquivalentTo(new ChangeWindow() { MinChangeVersion = 81, MaxChangeVersion = 100 });
                    pageMessages.ElementAt(5).ChangeWindow.ShouldBeEquivalentTo(new ChangeWindow() { MinChangeVersion = 81, MaxChangeVersion = 100 });

                    pageMessages.ElementAt(0).Offset.ShouldBe(50);
                    pageMessages.ElementAt(0).Limit.ShouldBe(_pageSize);

                    pageMessages.ElementAt(1).Offset.ShouldBe(0);
                    pageMessages.ElementAt(1).Limit.ShouldBe(_pageSize);

                    pageMessages.ElementAt(2).Offset.ShouldBe(50);
                    pageMessages.ElementAt(2).Limit.ShouldBe(_pageSize);

                    pageMessages.ElementAt(3).Offset.ShouldBe(0);
                    pageMessages.ElementAt(3).Limit.ShouldBe(_pageSize);

                    pageMessages.ElementAt(4).Offset.ShouldBe(50);
                    pageMessages.ElementAt(4).Limit.ShouldBe(_pageSize);

                    pageMessages.ElementAt(5).Offset.ShouldBe(0);
                    pageMessages.ElementAt(5).Limit.ShouldBe(_pageSize);
                }
            }

            [TestFixture]
            public class Given_100_records_and_ChangeVersion_page_size_equal_40_and_pageSize_equal_to_30 : TestFixtureAsyncBase
            {
                private IFakeHttpRequestHandler _fakeSourceRequestHandler;
                private EdFiApiChangeVersionReversePagingStreamResourcePageMessageProducer _messageProducer;
                private Options _options;
                private int _pageSize = 30;

                protected override async Task ArrangeAsync()
                {
                    // -----------------------------------------------------------------
                    //                      Source Requests
                    // -----------------------------------------------------------------
                    var sourceResourceFaker = TestHelpers.GetGenericResourceFaker();

                    var suppliedSourceResources = sourceResourceFaker.Generate(100);

                    // Prepare the fake source API endpoint
                    _fakeSourceRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler()

                        // Test-specific mocks
                        .AvailableChangeVersions(1100)
                        .ResourceCount(responseTotalCountHeader: 100);

                    var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(
                        includeOnly: new[] { "schools" });

                    _options = TestHelpers.GetOptions();
                    _options.IncludeDescriptors = false; // Shorten test execution time
                    _options.UseChangeVersionPaging = true;
                    _options.UseReversePaging = true;
                    _options.StreamingPageSize = _pageSize;
                    _options.ChangeVersionPagingWindowSize = 40;

                    await Task.Yield();

                    EdFiApiClient SourceApiClientFactory() =>
                        new EdFiApiClient(
                            "TestSource",
                            sourceApiConnectionDetails,
                            bearerTokenRefreshMinutes: 27,
                            ignoreSslErrors: true,
                            httpClientHandler: new HttpClientHandlerFakeBridge(_fakeSourceRequestHandler));


                    var sourceEdFiApiClientProvider = new EdFiApiClientProvider(new Lazy<EdFiApiClient>(SourceApiClientFactory));

                    _messageProducer = new EdFiApiChangeVersionReversePagingStreamResourcePageMessageProducer(
                        new EdFiApiSourceTotalCountProvider(sourceEdFiApiClientProvider));
                }

                [TestCase("schools")]
                public async Task Should_produce_12_messages_with_3_change_query_windows(string resourceCollectionName)
                {
                    StreamResourceMessage msg = new StreamResourceMessage
                    {
                        ChangeWindow = new ChangeWindow()
                        {
                            MaxChangeVersion = 100,
                            MinChangeVersion = 0

                        },
                        ResourceUrl = $"{EdFiApiConstants.DataManagementApiSegment}{TestHelpers.AnyResourcePattern}",
                        PageSize = _pageSize
                    };

                    var pageMessages = await _messageProducer.ProduceMessagesAsync<string>(msg, _options, null, null, new CancellationToken());

                    pageMessages.ElementAt(0).ChangeWindow.ShouldBeEquivalentTo(new ChangeWindow() { MinChangeVersion = 0, MaxChangeVersion = 40 });
                    pageMessages.ElementAt(1).ChangeWindow.ShouldBeEquivalentTo(new ChangeWindow() { MinChangeVersion = 0, MaxChangeVersion = 40 });
                    pageMessages.ElementAt(2).ChangeWindow.ShouldBeEquivalentTo(new ChangeWindow() { MinChangeVersion = 0, MaxChangeVersion = 40 });
                    pageMessages.ElementAt(3).ChangeWindow.ShouldBeEquivalentTo(new ChangeWindow() { MinChangeVersion = 0, MaxChangeVersion = 40 });

                    pageMessages.ElementAt(4).ChangeWindow.ShouldBeEquivalentTo(new ChangeWindow() { MinChangeVersion = 41, MaxChangeVersion = 80 });
                    pageMessages.ElementAt(5).ChangeWindow.ShouldBeEquivalentTo(new ChangeWindow() { MinChangeVersion = 41, MaxChangeVersion = 80 });
                    pageMessages.ElementAt(6).ChangeWindow.ShouldBeEquivalentTo(new ChangeWindow() { MinChangeVersion = 41, MaxChangeVersion = 80 });
                    pageMessages.ElementAt(7).ChangeWindow.ShouldBeEquivalentTo(new ChangeWindow() { MinChangeVersion = 41, MaxChangeVersion = 80 });

                    pageMessages.ElementAt(8).ChangeWindow.ShouldBeEquivalentTo(new ChangeWindow() { MinChangeVersion = 81, MaxChangeVersion = 100 });
                    pageMessages.ElementAt(9).ChangeWindow.ShouldBeEquivalentTo(new ChangeWindow() { MinChangeVersion = 81, MaxChangeVersion = 100 });
                    pageMessages.ElementAt(10).ChangeWindow.ShouldBeEquivalentTo(new ChangeWindow() { MinChangeVersion = 81, MaxChangeVersion = 100 });
                    pageMessages.ElementAt(11).ChangeWindow.ShouldBeEquivalentTo(new ChangeWindow() { MinChangeVersion = 81, MaxChangeVersion = 100 });

                    pageMessages.ElementAt(0).Offset.ShouldBe(70);
                    pageMessages.ElementAt(0).Limit.ShouldBe(_pageSize);

                    pageMessages.ElementAt(1).Offset.ShouldBe(40);
                    pageMessages.ElementAt(1).Limit.ShouldBe(_pageSize);

                    pageMessages.ElementAt(2).Offset.ShouldBe(10);
                    pageMessages.ElementAt(2).Limit.ShouldBe(_pageSize);

                    pageMessages.ElementAt(3).Offset.ShouldBe(0);
                    pageMessages.ElementAt(3).Limit.ShouldBe(10);

                    pageMessages.ElementAt(4).Offset.ShouldBe(70);
                    pageMessages.ElementAt(4).Limit.ShouldBe(_pageSize);

                    pageMessages.ElementAt(5).Offset.ShouldBe(40);
                    pageMessages.ElementAt(5).Limit.ShouldBe(_pageSize);

                    pageMessages.ElementAt(6).Offset.ShouldBe(10);
                    pageMessages.ElementAt(6).Limit.ShouldBe(_pageSize);

                    pageMessages.ElementAt(7).Offset.ShouldBe(0);
                    pageMessages.ElementAt(7).Limit.ShouldBe(10);

                    pageMessages.ElementAt(8).Offset.ShouldBe(70);
                    pageMessages.ElementAt(8).Limit.ShouldBe(_pageSize);

                    pageMessages.ElementAt(9).Offset.ShouldBe(40);
                    pageMessages.ElementAt(9).Limit.ShouldBe(_pageSize);

                    pageMessages.ElementAt(10).Offset.ShouldBe(10);
                    pageMessages.ElementAt(10).Limit.ShouldBe(_pageSize);

                    pageMessages.ElementAt(11).Offset.ShouldBe(0);
                    pageMessages.ElementAt(11).Limit.ShouldBe(10);
                }
            }

        }
    }
}
