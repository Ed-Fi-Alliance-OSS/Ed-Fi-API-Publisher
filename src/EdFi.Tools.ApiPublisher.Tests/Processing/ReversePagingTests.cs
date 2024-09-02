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
        public class When_producing_messages_with_reverse_paging
        {
            [TestFixture]
            public class Given_a_MaxChangeVersion_equal_to_100_and_ChangeVersion_page_size_equal_40_and_pageSize_equal_to_40 : TestFixtureAsyncBase
            {
                private ChangeProcessor _changeProcessor;
                private IFakeHttpRequestHandler _fakeTargetRequestHandler;
                private IFakeHttpRequestHandler _fakeSourceRequestHandler;
                private ChangeProcessorConfiguration _changeProcessorConfiguration;
                private EdFiApiChangeVersionReversePagingStreamResourcePageMessageProducer _messageProducer;
                private Options _options;

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
                        .ResourceCount(responseTotalCountHeader: 100)
                        .GetResourceData($"{EdFiApiConstants.DataManagementApiSegment}{TestHelpers.AnyResourcePattern}", suppliedSourceResources)
                        .GetResourceData($"{EdFiApiConstants.DataManagementApiSegment}{TestHelpers.AnyResourcePattern}/deletes", Array.Empty<object>());

                    var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(
                        includeOnly: new[] { "schools" });

                    _options = TestHelpers.GetOptions();
                    _options.IncludeDescriptors = false; // Shorten test execution time
                    _options.UseChangeVersionPaging = true;
                    _options.useReversePaging = true;
                    _options.StreamingPageSize = 40;
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
                public async Task Should_produce_messages_correctly(string resourceCollectionName)
                {
                    StreamResourceMessage msg = new StreamResourceMessage
                    {
                        ChangeWindow = new ChangeWindow()
                        {
                            MaxChangeVersion = 100,
                            MinChangeVersion = 0

                        },
                        ResourceUrl = $"{EdFiApiConstants.DataManagementApiSegment}{TestHelpers.AnyResourcePattern}",
                        PageSize = 40
                    };

                    var pageMessages = await _messageProducer.ProduceMessagesAsync<string>(msg, _options, null, null, new CancellationToken());

                    pageMessages.ElementAt(0).ChangeWindow.ShouldBeEquivalentTo(new ChangeWindow() { MinChangeVersion = 0, MaxChangeVersion = 40 });
                    pageMessages.ElementAt(3).ChangeWindow.ShouldBeEquivalentTo(new ChangeWindow() { MinChangeVersion = 41, MaxChangeVersion = 80 });
                    pageMessages.ElementAt(6).ChangeWindow.ShouldBeEquivalentTo(new ChangeWindow() { MinChangeVersion = 81, MaxChangeVersion = 100 });

                    pageMessages.ElementAt(0).Offset.ShouldBe(60);
                    pageMessages.ElementAt(0).Limit.ShouldBe(40);

                    pageMessages.ElementAt(1).Offset.ShouldBe(20);
                    pageMessages.ElementAt(1).Limit.ShouldBe(40);

                    pageMessages.ElementAt(2).Offset.ShouldBe(0);
                    pageMessages.ElementAt(2).Limit.ShouldBe(20);

                    pageMessages.ElementAt(3).Offset.ShouldBe(60);
                    pageMessages.ElementAt(3).Limit.ShouldBe(40);

                    pageMessages.ElementAt(4).Offset.ShouldBe(20);
                    pageMessages.ElementAt(4).Limit.ShouldBe(40);

                    pageMessages.ElementAt(5).Offset.ShouldBe(0);
                    pageMessages.ElementAt(5).Limit.ShouldBe(20);

                    pageMessages.ElementAt(6).Offset.ShouldBe(60);
                    pageMessages.ElementAt(6).Limit.ShouldBe(40);

                    pageMessages.ElementAt(7).Offset.ShouldBe(20);
                    pageMessages.ElementAt(7).Limit.ShouldBe(40);

                    pageMessages.ElementAt(8).Offset.ShouldBe(0);
                    pageMessages.ElementAt(8).Limit.ShouldBe(20);
                }
            }


            [TestFixture]
            public class Given_a_MaxChangeVersion_equal_to_100_and_ChangeVersion_page_size_equal_40_and_pageSize_equal_to_50 : TestFixtureAsyncBase
            {
                private ChangeProcessor _changeProcessor;
                private IFakeHttpRequestHandler _fakeTargetRequestHandler;
                private IFakeHttpRequestHandler _fakeSourceRequestHandler;
                private ChangeProcessorConfiguration _changeProcessorConfiguration;
                private EdFiApiChangeVersionReversePagingStreamResourcePageMessageProducer _messageProducer;
                private Options _options;

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
                        .ResourceCount(responseTotalCountHeader: 100)
                        .GetResourceData($"{EdFiApiConstants.DataManagementApiSegment}{TestHelpers.AnyResourcePattern}", suppliedSourceResources)
                        .GetResourceData($"{EdFiApiConstants.DataManagementApiSegment}{TestHelpers.AnyResourcePattern}/deletes", Array.Empty<object>());

                    var sourceApiConnectionDetails = TestHelpers.GetSourceApiConnectionDetails(
                        includeOnly: new[] { "schools" });

                    _options = TestHelpers.GetOptions();
                    _options.IncludeDescriptors = false; // Shorten test execution time
                    _options.UseChangeVersionPaging = true;
                    _options.useReversePaging = true;
                    _options.StreamingPageSize = 50;
                    _options.ChangeVersionPagingWindowSize = 50;

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
                public async Task Should_produce_messages_correctly(string resourceCollectionName)
                {
                    var pageSize = 50;
                    StreamResourceMessage msg = new StreamResourceMessage
                    {
                        ChangeWindow = new ChangeWindow()
                        {
                            MaxChangeVersion = 100,
                            MinChangeVersion = 0

                        },
                        ResourceUrl = $"{EdFiApiConstants.DataManagementApiSegment}{TestHelpers.AnyResourcePattern}",
                        PageSize = pageSize
                    };

                    var pageMessages = await _messageProducer.ProduceMessagesAsync<string>(msg, _options, null, null, new CancellationToken());

                    pageMessages.ElementAt(0).ChangeWindow.ShouldBeEquivalentTo(new ChangeWindow() { MinChangeVersion = 0, MaxChangeVersion = 50 });
                    pageMessages.ElementAt(2).ChangeWindow.ShouldBeEquivalentTo(new ChangeWindow() { MinChangeVersion = 51, MaxChangeVersion = 100 });

                    pageMessages.ElementAt(0).Offset.ShouldBe(50);
                    pageMessages.ElementAt(0).Limit.ShouldBe(pageSize);

                    pageMessages.ElementAt(1).Offset.ShouldBe(0);
                    pageMessages.ElementAt(1).Limit.ShouldBe(pageSize);

                    pageMessages.ElementAt(2).Offset.ShouldBe(50);
                    pageMessages.ElementAt(2).Limit.ShouldBe(pageSize);

                    pageMessages.ElementAt(3).Offset.ShouldBe(0);
                    pageMessages.ElementAt(3).Limit.ShouldBe(pageSize);
                }
            }
        }
    }
}
