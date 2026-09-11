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
using Serilog.Sinks.TestCorrelator;
using Serilog.Events;
using System.Text;

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
        public async Task Successful_non_200_partitions_response_should_report_failure_for_offset_fallback()
        {
            // Only HTTP 200 with a pageTokens array is the documented partitions response (APIPUB-139)
            var (producer, _, _) = Create(() => new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(@"{""pageTokens"":[""t1""]}", Encoding.UTF8, "application/json")
            });

            var (success, messages) = await producer.TryProduceMessagesAsync<object>(
                CreateResourceMessage(), TestHelpers.GetOptions(), new BufferBlock<ErrorItemMessage>(), null, CancellationToken.None);

            success.ShouldBeFalse();
            messages.ShouldBeNull();
        }

        [Test]
        public async Task Failed_partitions_response_body_should_be_capped_in_the_log()
        {
            // An error body is logged to help diagnose the fallback, but it is not under our control: cap it so a
            // large (or sensitive) response cannot flood the log
            TestHelpers.InitializeLogging();
            string body = new string('x', 5_000);

            var (producer, _, _) = Create(() => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent(body) });

            using (TestCorrelator.CreateContext())
            {
                var (success, _) = await producer.TryProduceMessagesAsync<object>(
                    CreateResourceMessage(), TestHelpers.GetOptions(), new BufferBlock<ErrorItemMessage>(), null, CancellationToken.None);

                success.ShouldBeFalse();

                var logEvent = TestCorrelator.GetLogEventsFromCurrentContext()
                    .Single(e => e.MessageTemplate.Text.Contains("Partitions request returned status"));

                string loggedContent = logEvent.Properties["Content"].ToString();
                // The rendered property is the capped body in quotes plus a short truncation marker
                loggedContent.Length.ShouldBeLessThanOrEqualTo(EdFiApiCursorPagingStreamResourcePageMessageProducer.MaxLoggedErrorBodyLength + 64);
                loggedContent.ShouldContain("truncated");
            }
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
        public async Task Total_count_request_should_not_wait_for_the_partitions_response()
        {
            // The partitions and total count requests are independent. Issued in series they cost the cursor pre-page
            // phase a full extra round trip per resource (1.2 s vs 0.65 s for offset paging against a local ODS/API 7.3),
            // so the count must already be in flight while the partitions response is outstanding.
            using var countRequested = new ManualResetEventSlim(false);
            bool countObservedBeforePartitionsResponded = false;

            var (producer, fake, _) = Create(() =>
            {
                countObservedBeforePartitionsResponded = countRequested.Wait(TimeSpan.FromSeconds(2));

                return FakeResponse.OK(new { pageTokens = new[] { "t1", "t2" } });
            });

            A.CallTo(() => fake.Get(A<string>.Ignored, A<HttpRequestMessage>.That.Matches(msg => msg.RequestUri.ParseQueryString()["totalCount"] == "true")))
                .ReturnsLazily(() =>
                {
                    countRequested.Set();

                    return FakeResponse.OK("[]").AppendHeaders(("Total-Count", "10"));
                });

            var (success, messages) = await producer.TryProduceMessagesAsync<object>(
                CreateResourceMessage(), TestHelpers.GetOptions(), new BufferBlock<ErrorItemMessage>(), null, CancellationToken.None);

            success.ShouldBeTrue();
            messages.Count().ShouldBe(2);
            countObservedBeforePartitionsResponded.ShouldBeTrue("the total count request should be in flight while the partitions request is outstanding");
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

        [Test]
        public async Task Header_phase_timeout_on_the_partitions_request_should_fall_back_to_offset_paging()
        {
            // HttpClient.Timeout expiring while waiting for the headers surfaces as a TaskCanceledException the run did
            // not ask for; it is a failed partitions request, not a reason to end the run
            TestHelpers.InitializeLogging();
            var (producer, _, _) = Create(() => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.", new TimeoutException()));

            using (TestCorrelator.CreateContext())
            {
                var (success, messages) = await producer.TryProduceMessagesAsync<object>(
                    CreateResourceMessage(), TestHelpers.GetOptions(), new BufferBlock<ErrorItemMessage>(), null, CancellationToken.None);

                success.ShouldBeFalse();
                messages.ShouldBeNull();

                TestCorrelator.GetLogEventsFromCurrentContext()
                    .ShouldContain(e => e.Level == LogEventLevel.Warning && e.MessageTemplate.Text.Contains("Partitions request failed"));
            }
        }

        [Test]
        public async Task Run_cancellation_during_the_partitions_request_should_propagate()
        {
            var cancellation = new CancellationTokenSource();
            var (producer, _, _) = Create(() =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            });

            await Should.ThrowAsync<OperationCanceledException>(() => producer.TryProduceMessagesAsync<object>(
                CreateResourceMessage(), TestHelpers.GetOptions(), new BufferBlock<ErrorItemMessage>(), null, cancellation.Token));
        }

        [Test]
        public async Task Null_or_empty_page_token_should_fall_back_to_offset_paging_instead_of_skipping_a_partition()
        {
            TestHelpers.InitializeLogging();
            var (producer, _, _) = Create(() => FakeResponse.OK(new { pageTokens = new[] { "t1", null, "t3" } }));

            using (TestCorrelator.CreateContext())
            {
                var (success, messages) = await producer.TryProduceMessagesAsync<object>(
                    CreateResourceMessage(), TestHelpers.GetOptions(), new BufferBlock<ErrorItemMessage>(), null, CancellationToken.None);

                success.ShouldBeFalse();
                messages.ShouldBeNull();

                TestCorrelator.GetLogEventsFromCurrentContext()
                    .ShouldContain(e => e.Level == LogEventLevel.Warning && e.MessageTemplate.Text.Contains("null or empty page token"));
            }
        }

        [Test]
        public async Task Stalled_partitions_response_body_should_time_out_and_fall_back_to_offset_paging()
        {
            // RequestHelpers reads with ResponseHeadersRead, so HttpClient.Timeout covers only the headers; the body
            // read must carry its own deadline or a stalled source would hang the producer short of its fallback
            TestHelpers.InitializeLogging();
            var fake = TestHelpers.GetFakeBaselineSourceApiRequestHandler().ResourceCount(responseTotalCountHeader: 10);

            A.CallTo(() => fake.Get(A<string>.Ignored, A<HttpRequestMessage>.That.Matches(msg => msg.RequestUri.LocalPath == PartitionsPath)))
                .ReturnsLazily(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) });

            EdFiApiClient ClientFactory() =>
                new EdFiApiClient("TestSource", TestHelpers.GetSourceApiConnectionDetails(), 27, true,
                    httpClientHandler: new HttpClientHandlerFakeBridge(fake));

            var clientProvider = new EdFiApiClientProvider(new Lazy<EdFiApiClient>(ClientFactory));
            clientProvider.GetApiClient().HttpClient.Timeout = TimeSpan.FromMilliseconds(500);

            var producer = new EdFiApiCursorPagingStreamResourcePageMessageProducer(clientProvider, new EdFiApiSourceTotalCountProvider(clientProvider));

            using (TestCorrelator.CreateContext())
            {
                var produceTask = producer.TryProduceMessagesAsync<object>(
                    CreateResourceMessage(), TestHelpers.GetOptions(), new BufferBlock<ErrorItemMessage>(), null, CancellationToken.None);

                (await Task.WhenAny(produceTask, Task.Delay(TimeSpan.FromSeconds(15)))).ShouldBe(produceTask, "the stalled body read should have been abandoned by the deadline");

                var (success, messages) = await produceTask;

                success.ShouldBeFalse();
                messages.ShouldBeNull();

                TestCorrelator.GetLogEventsFromCurrentContext()
                    .ShouldContain(e => e.Level == LogEventLevel.Warning && e.MessageTemplate.Text.Contains("Partitions request failed"));
            }
        }

        /// <summary>
        /// A readable stream whose reads never complete until cancelled, standing in for a source that sends headers and then stalls.
        /// </summary>
        private sealed class StallingStream : System.IO.Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush() { }
            public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
