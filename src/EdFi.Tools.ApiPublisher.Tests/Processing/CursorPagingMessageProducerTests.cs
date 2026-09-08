// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Counting;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.MessageProducers;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Blocks;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using EdFi.Tools.ApiPublisher.Tests.Extensions;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using NUnit.Framework;
using Shouldly;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// Verifies the partition-aware producer (APIPUB-139): one /partitions request per resource (with the change
    /// window), one page message per returned token, zero messages for an empty pageTokens array, a retried
    /// transient failure, and a reported failure (for offset fallback) on a non-success response.
    /// </summary>
    [TestFixture]
    public class CursorPagingMessageProducerTests
    {
        private const string Students = "/ed-fi/students";
        private const string PartitionsPath = "/data/v3/ed-fi/students/partitions";

        private static (EdFiApiCursorPagingStreamResourcePageMessageProducer producer, IFakeHttpRequestHandler fake, ConcurrentQueue<HttpRequestMessage> partitionRequests)
            Create(Func<HttpResponseMessage> partitionsResponse, int totalCount = 10)
        {
            var fake = TestHelpers.GetFakeBaselineSourceApiRequestHandler().ResourceCount(responseTotalCountHeader: totalCount);
            var partitionRequests = new ConcurrentQueue<HttpRequestMessage>();

            A.CallTo(() => fake.Get(A<string>.Ignored, A<HttpRequestMessage>.That.Matches(msg => msg.RequestUri.LocalPath == PartitionsPath)))
                .ReturnsLazily((string _, HttpRequestMessage request) =>
                {
                    partitionRequests.Enqueue(request);
                    return partitionsResponse();
                });

            EdFiApiClient ClientFactory() =>
                new EdFiApiClient("TestSource", TestHelpers.GetSourceApiConnectionDetails(), 27, true,
                    httpClientHandler: new HttpClientHandlerFakeBridge(fake));

            var clientProvider = new EdFiApiClientProvider(new Lazy<EdFiApiClient>(ClientFactory));

            return (new EdFiApiCursorPagingStreamResourcePageMessageProducer(clientProvider, new EdFiApiSourceTotalCountProvider(clientProvider)), fake, partitionRequests);
        }

        private static StreamResourceMessage CreateResourceMessage(ChangeWindow changeWindow = null) =>
            new StreamResourceMessage
            {
                ResourceUrl = Students,
                PageSize = 500,
                ChangeWindow = changeWindow,
                CancellationSource = new CancellationTokenSource(),
                HasAuthorizationRetryPipeline = true,
            };

        [Test]
        public async Task Each_returned_token_should_become_one_page_message()
        {
            var (producer, _, requests) = Create(() => FakeResponse.OK(new { pageTokens = new[] { "t1", "t2", "t3" } }));
            var options = TestHelpers.GetOptions();
            options.CursorPagingPartitionCount = 4;

            var (success, messages) = await producer.TryProduceMessagesAsync<object>(
                CreateResourceMessage(new ChangeWindow { MinChangeVersion = 10, MaxChangeVersion = 20 }),
                options, new BufferBlock<ErrorItemMessage>(), null, CancellationToken.None);

            success.ShouldBeTrue();
            var pages = messages.ToArray();
            pages.Length.ShouldBe(3);
            pages.Select(p => p.PageToken).ShouldBe(new[] { "t1", "t2", "t3" });
            pages.Select(p => p.PartitionIndex).ShouldBe(new int?[] { 1, 2, 3 });
            pages.All(p => p.PageSize == 500 && p.Offset is null && p.Limit is null && p.ResourceUrl == Students && p.HasAuthorizationRetryPipeline).ShouldBeTrue();
            pages.All(p => p.ChangeWindow.MinChangeVersion == 10 && p.ChangeWindow.MaxChangeVersion == 20).ShouldBeTrue();

            requests.Count.ShouldBe(1);
            requests.Single().RequestUri.ParseQueryString()["number"].ShouldBe("4");
            requests.Single().RequestUri.ParseQueryString()["minChangeVersion"].ShouldBe("10");
            requests.Single().RequestUri.ParseQueryString()["maxChangeVersion"].ShouldBe("20");
        }

        [Test]
        public async Task Empty_pageTokens_should_produce_zero_messages_without_error()
        {
            var (producer, _, _) = Create(() => FakeResponse.OK(new { pageTokens = Array.Empty<string>() }));
            var errors = new BufferBlock<ErrorItemMessage>();

            var (success, messages) = await producer.TryProduceMessagesAsync<object>(
                CreateResourceMessage(), TestHelpers.GetOptions(), errors, null, CancellationToken.None);

            success.ShouldBeTrue();
            messages.ShouldBeEmpty();
            errors.Count.ShouldBe(0);
        }

        [Test]
        public async Task Non_success_partitions_response_should_report_failure_for_offset_fallback()
        {
            var (producer, _, _) = Create(FakeResponse.NotFound);

            var (success, messages) = await producer.TryProduceMessagesAsync<object>(
                CreateResourceMessage(), TestHelpers.GetOptions(), new BufferBlock<ErrorItemMessage>(), null, CancellationToken.None);

            success.ShouldBeFalse();
            messages.ShouldBeNull();
        }

        [Test]
        public async Task Transient_partitions_failure_should_be_retried()
        {
            int attempts = 0;
            var (producer, _, requests) = Create(() => Interlocked.Increment(ref attempts) == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : FakeResponse.OK(new { pageTokens = new[] { "t1" } }));

            var (success, messages) = await producer.TryProduceMessagesAsync<object>(
                CreateResourceMessage(), TestHelpers.GetOptions(), new BufferBlock<ErrorItemMessage>(), null, CancellationToken.None);

            success.ShouldBeTrue();
            messages.Count().ShouldBe(1);
            requests.Count.ShouldBe(2);
        }

        [Test]
        public async Task Failed_total_count_should_produce_zero_messages_like_the_offset_producer()
        {
            var (producer, fake, _) = Create(() => FakeResponse.OK(new { pageTokens = new[] { "t1" } }));

            A.CallTo(() => fake.Get(A<string>.Ignored, A<HttpRequestMessage>.That.Matches(msg => msg.RequestUri.ParseQueryString()["totalCount"] == "true")))
                .ReturnsLazily(() => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") });

            var (success, messages) = await producer.TryProduceMessagesAsync<object>(
                CreateResourceMessage(), TestHelpers.GetOptions(), new BufferBlock<ErrorItemMessage>(), null, CancellationToken.None);

            success.ShouldBeTrue();
            messages.ShouldBeEmpty();
        }
    }
}
