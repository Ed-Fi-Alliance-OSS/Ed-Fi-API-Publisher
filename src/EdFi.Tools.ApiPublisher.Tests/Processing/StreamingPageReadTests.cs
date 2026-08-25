// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Connections.Api.DependencyResolution;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.MessageHandlers;
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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// Proves the APIPUB-134 streaming page read is genuine at the handler level: the response body is
    /// consumed in a single forward-only pass with no whole-page buffering, the response is disposed after
    /// parsing, the final-page continuation count flows through the seam callback, and the error branches
    /// keep their pre-streaming behavior.
    /// </summary>
    [TestFixture]
    public class StreamingPageReadTests
    {
        // ForwardOnlyStream and InstrumentedJsonContent live in Tests\Helpers (shared with the
        // retry-disposal and count-provider tests)

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

        private static (EdFiApiStreamResourcePageMessageHandler handler, IFakeHttpRequestHandler fakeRequestHandler)
            CreateHandler()
        {
            var fakeRequestHandler = TestHelpers.GetFakeBaselineSourceApiRequestHandler();

            EdFiApiClient SourceApiClientFactory() =>
                new EdFiApiClient(
                    "TestSource",
                    TestHelpers.GetSourceApiConnectionDetails(),
                    bearerTokenRefreshMinutes: 27,
                    ignoreSslErrors: true,
                    httpClientHandler: new HttpClientHandlerFakeBridge(fakeRequestHandler));

            var sourceClientProvider = A.Fake<ISourceEdFiApiClientProvider>();
            A.CallTo(() => sourceClientProvider.GetApiClient()).Returns(SourceApiClientFactory());

            return (new EdFiApiStreamResourcePageMessageHandler(sourceClientProvider), fakeRequestHandler);
        }

        private static void SetupPageGet(
            IFakeHttpRequestHandler fakeRequestHandler,
            string resourceLocalPath,
            Func<HttpResponseMessage> createResponse)
        {
            A.CallTo(
                    () => fakeRequestHandler.Get(
                        A<string>.Ignored,
                        A<HttpRequestMessage>.That.Matches(msg => msg.RequestUri.LocalPath == resourceLocalPath)))
                .ReturnsLazily(createResponse);
        }

        private static StreamResourcePageMessage<PostItemMessage> CreatePageMessage(int limit, bool isFinalPage)
        {
            return new StreamResourcePageMessage<PostItemMessage>
            {
                ResourceUrl = "/ed-fi/students",
                Offset = 0,
                Limit = limit,
                IsFinalPage = isFinalPage,
                CancellationSource = new CancellationTokenSource(),
                CreateProcessDataMessages = CreatePostFactory().CreateProcessDataMessages,
            };
        }

        [Test]
        public async Task Page_body_should_be_streamed_in_a_single_pass_without_buffering_and_disposed()
        {
            TestHelpers.InitializeLogging();

            var (handler, fakeRequestHandler) = CreateHandler();

            var content = new InstrumentedJsonContent(@"[{""id"":""1""},{""id"":""2""},{""id"":""3""}]");

            SetupPageGet(
                fakeRequestHandler,
                "/data/v3/ed-fi/students",
                () => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

            var message = CreatePageMessage(limit: 50, isFinalPage: false);
            var errorBlock = new BufferBlock<ErrorItemMessage>();

            var itemMessages = (await handler.HandleStreamResourcePageAsync(message, TestHelpers.GetOptions(), errorBlock))
                .ToArray();

            itemMessages.Length.ShouldBe(3);
            itemMessages.Select(m => m.Item["id"]!.Value<string>()).ShouldBe(new[] { "1", "2", "3" });

            // The whole-body buffering path (ReadAsStringAsync / ResponseContentRead) was never invoked
            content.BufferingAttempted.ShouldBeFalse();

            // The body was consumed as exactly one forward-only stream, and everything was disposed
            content.StreamsCreated.ShouldBe(1);
            content.LastStream.Disposed.ShouldBeTrue();
            content.ContentDisposed.ShouldBeTrue();

            errorBlock.Count.ShouldBe(0);
        }

        [Test]
        public async Task Final_page_continuation_should_issue_another_request_when_the_page_is_full()
        {
            TestHelpers.InitializeLogging();

            var (handler, fakeRequestHandler) = CreateHandler();

            int pageRequestCount = 0;

            var pages = new Queue<string>(new[]
            {
                @"[{""id"":""1""},{""id"":""2""}]",
                @"[{""id"":""3""}]",
            });

            SetupPageGet(
                fakeRequestHandler,
                "/data/v3/ed-fi/students",
                () =>
                {
                    pageRequestCount++;

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new InstrumentedJsonContent(pages.Dequeue())
                    };
                });

            // Final page comes back full (2 items == limit), so the handler should continue to the next page
            var message = CreatePageMessage(limit: 2, isFinalPage: true);
            var errorBlock = new BufferBlock<ErrorItemMessage>();

            var itemMessages = (await handler.HandleStreamResourcePageAsync(message, TestHelpers.GetOptions(), errorBlock))
                .ToArray();

            pageRequestCount.ShouldBe(2);
            itemMessages.Length.ShouldBe(3);
            itemMessages.Select(m => m.Item["id"]!.Value<string>()).ShouldBe(new[] { "1", "2", "3" });
        }

        [Test]
        public async Task Error_responses_should_still_carry_the_response_body_text()
        {
            TestHelpers.InitializeLogging();

            var (handler, fakeRequestHandler) = CreateHandler();

            const string ErrorBody = @"{""message"":""Resource not found.""}";

            SetupPageGet(
                fakeRequestHandler,
                "/data/v3/ed-fi/students",
                () => new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(ErrorBody, Encoding.UTF8, "application/json")
                });

            var message = CreatePageMessage(limit: 50, isFinalPage: false);
            var errorBlock = new BufferBlock<ErrorItemMessage>();

            var itemMessages = await handler.HandleStreamResourcePageAsync(message, TestHelpers.GetOptions(), errorBlock);

            itemMessages.ShouldBeEmpty();

            errorBlock.TryReceive(out var error).ShouldBeTrue();
            error.ResponseStatus.ShouldBe(HttpStatusCode.NotFound);
            error.ResponseContent.ShouldBe(ErrorBody);
        }

        [Test]
        public async Task Malformed_page_should_contribute_no_messages_and_publish_the_parse_failure()
        {
            TestHelpers.InitializeLogging();

            // A valid prefix followed by truncation: streaming yields two items before the failure,
            // but the page must contribute zero messages (whole-page atomicity)
            var content = new InstrumentedJsonContent(@"[{""id"":""1""},{""id"":""2""},{""id"":");

            var (handler, fakeRequestHandler) = CreateHandler();

            SetupPageGet(
                fakeRequestHandler,
                "/data/v3/ed-fi/students",
                () => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

            var message = CreatePageMessage(limit: 50, isFinalPage: false);
            var errorBlock = new BufferBlock<ErrorItemMessage>();

            var itemMessages = await handler.HandleStreamResourcePageAsync(message, TestHelpers.GetOptions(), errorBlock);

            itemMessages.ShouldBeEmpty();

            errorBlock.TryReceive(out var error).ShouldBeTrue();
            error.Exception.ShouldBeOfType<JsonReaderException>();

            // The page was streamed, not buffered, so the error carries no body text
            error.ResponseContent.ShouldBeNull();

            // The response is still disposed after the failure
            content.ContentDisposed.ShouldBeTrue();
        }

        [Test]
        public async Task Transient_retry_responses_should_be_disposed()
        {
            TestHelpers.InitializeLogging();

            var (handler, fakeRequestHandler) = CreateHandler();

            // First attempt fails transiently; the retry succeeds
            var transientContent = new InstrumentedJsonContent(@"{""message"":""temporarily unavailable""}");
            int attempts = 0;

            SetupPageGet(
                fakeRequestHandler,
                "/data/v3/ed-fi/students",
                () => Interlocked.Increment(ref attempts) == 1
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = transientContent }
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new InstrumentedJsonContent(@"[{""id"":""1""}]")
                    });

            var message = CreatePageMessage(limit: 50, isFinalPage: false);
            var errorBlock = new BufferBlock<ErrorItemMessage>();

            var itemMessages = (await handler.HandleStreamResourcePageAsync(message, TestHelpers.GetOptions(), errorBlock))
                .ToArray();

            attempts.ShouldBe(2);
            itemMessages.Length.ShouldBe(1);
            errorBlock.Count.ShouldBe(0);

            // With ResponseHeadersRead, an undisposed transient response would pin a connection --
            // the retry callback must dispose the failed response being retried
            transientContent.ContentDisposed.ShouldBeTrue();
        }

        [Test]
        public async Task Cancellation_during_transient_retry_should_abandon_the_page_fetch_without_publishing_an_error()
        {
            TestHelpers.InitializeLogging();

            var (handler, fakeRequestHandler) = CreateHandler();

            var message = CreatePageMessage(limit: 50, isFinalPage: false);

            int attempts = 0;

            SetupPageGet(
                fakeRequestHandler,
                "/data/v3/ed-fi/students",
                () =>
                {
                    Interlocked.Increment(ref attempts);

                    // Cancel while the retry policy is heading into its backoff delay
                    message.CancellationSource.Cancel();

                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        Content = new StringContent("{}", Encoding.UTF8, "application/json")
                    };
                });

            // Without cancellation aborting the retry backoff, this configuration would take minutes
            var options = TestHelpers.GetOptions();
            options.MaxRetryAttempts = 10;
            options.RetryStartingDelayMilliseconds = 10_000;

            var errorBlock = new BufferBlock<ErrorItemMessage>();

            var handlerTask = handler.HandleStreamResourcePageAsync(message, options, errorBlock);
            (await Task.WhenAny(handlerTask, Task.Delay(TimeSpan.FromSeconds(30)))).ShouldBe(handlerTask);

            // Graceful cancellation: no items, no further attempts, and no error published
            (await handlerTask).ShouldBeEmpty();
            attempts.ShouldBe(1);
            errorBlock.Count.ShouldBe(0);
        }

        /// <summary>
        /// A stream that serves a valid JSON prefix on the first read and then blocks until disposed,
        /// simulating a slow response body. Disposal (the cancellation-abort path) unblocks the read,
        /// which then throws.
        /// </summary>
        private class StallingStream : Stream
        {
            private readonly byte[] _prefix;
            private readonly SemaphoreSlim _readStalled;
            private readonly ManualResetEventSlim _unblock = new(false);
            private bool _prefixServed;

            public StallingStream(byte[] prefix, SemaphoreSlim readStalled)
            {
                _prefix = prefix;
                _readStalled = readStalled;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (!_prefixServed)
                {
                    _prefixServed = true;
                    Array.Copy(_prefix, 0, buffer, offset, _prefix.Length);

                    return _prefix.Length;
                }

                _readStalled.Release();
                _unblock.Wait(TimeSpan.FromSeconds(30));

                throw new ObjectDisposedException(nameof(StallingStream));
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                _unblock.Set();

                base.Dispose(disposing);
            }
        }

        [Test]
        public async Task Cancellation_during_a_stalled_body_read_should_abort_parsing_and_return_without_error()
        {
            TestHelpers.InitializeLogging();

            var (handler, fakeRequestHandler) = CreateHandler();

            // The body starts with one complete item, then stalls forever -- only cancellation can end the read
            var readStalled = new SemaphoreSlim(0);
            var stallingStream = new StallingStream(Encoding.UTF8.GetBytes(@"[{""id"":""1""},"), readStalled);

            SetupPageGet(
                fakeRequestHandler,
                "/data/v3/ed-fi/students",
                () =>
                {
                    var content = new StreamContent(stallingStream);
                    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
                });

            var message = CreatePageMessage(limit: 50, isFinalPage: false);
            var errorBlock = new BufferBlock<ErrorItemMessage>();

            // The parse blocks synchronously, so run the handler off the test thread
            var handlerTask = Task.Run(() => handler.HandleStreamResourcePageAsync(message, TestHelpers.GetOptions(), errorBlock));

            (await readStalled.WaitAsync(TimeSpan.FromSeconds(30))).ShouldBeTrue();

            // Cancel while the reader is blocked mid-body; the registered abort must dispose the stream,
            // unblocking the read, and the handler must treat the outcome as graceful cancellation
            message.CancellationSource.Cancel();

            (await Task.WhenAny(handlerTask, Task.Delay(TimeSpan.FromSeconds(30)))).ShouldBe(handlerTask);

            (await handlerTask).ShouldBeEmpty();
            errorBlock.Count.ShouldBe(0);
        }

        [Test]
        public async Task Exhausted_transient_retries_should_publish_the_final_failure_and_dispose_every_response()
        {
            TestHelpers.InitializeLogging();

            var (handler, fakeRequestHandler) = CreateHandler();

            const string ErrorBody = @"{""message"":""temporarily unavailable""}";
            var contents = new List<InstrumentedJsonContent>();

            SetupPageGet(
                fakeRequestHandler,
                "/data/v3/ed-fi/students",
                () =>
                {
                    var content = new InstrumentedJsonContent(ErrorBody);
                    contents.Add(content);

                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = content };
                });

            var message = CreatePageMessage(limit: 50, isFinalPage: false);
            var errorBlock = new BufferBlock<ErrorItemMessage>();

            // GetOptions configures MaxRetryAttempts = 2, so exhaustion means 3 attempts in total
            var itemMessages = await handler.HandleStreamResourcePageAsync(message, TestHelpers.GetOptions(), errorBlock);

            itemMessages.ShouldBeEmpty();
            contents.Count.ShouldBe(3);

            // The final failure is published with its status and body text
            errorBlock.TryReceive(out var error).ShouldBeTrue();
            error.ResponseStatus.ShouldBe(HttpStatusCode.ServiceUnavailable);
            error.ResponseContent.ShouldBe(ErrorBody);

            // Every response was disposed: the retried ones by the retry callback, the final one explicitly
            contents.ShouldAllBe(c => c.ContentDisposed);
        }

        [Test]
        public async Task Precancelled_page_message_should_return_no_items_without_requesting_or_publishing_anything()
        {
            TestHelpers.InitializeLogging();

            var (handler, fakeRequestHandler) = CreateHandler();

            int attempts = 0;

            SetupPageGet(
                fakeRequestHandler,
                "/data/v3/ed-fi/students",
                () =>
                {
                    Interlocked.Increment(ref attempts);

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("[]", Encoding.UTF8, "application/json")
                    };
                });

            var message = CreatePageMessage(limit: 50, isFinalPage: false);
            message.CancellationSource.Cancel();

            var errorBlock = new BufferBlock<ErrorItemMessage>();

            var itemMessages = await handler.HandleStreamResourcePageAsync(message, TestHelpers.GetOptions(), errorBlock);

            itemMessages.ShouldBeEmpty();
            attempts.ShouldBe(0);
            errorBlock.Count.ShouldBe(0);
        }
    }
}
