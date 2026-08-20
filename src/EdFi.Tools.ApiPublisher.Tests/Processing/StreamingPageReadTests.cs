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
        /// <summary>
        /// A non-seekable read-only stream that records whether it was disposed.
        /// </summary>
        private class ForwardOnlyStream : Stream
        {
            private readonly MemoryStream _inner;

            public ForwardOnlyStream(byte[] data)
            {
                _inner = new MemoryStream(data);
            }

            public bool Disposed { get; private set; }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                _inner.Dispose();

                base.Dispose(disposing);
            }
        }

        /// <summary>
        /// HttpContent that hands out a non-seekable stream for the streaming path and records whether the
        /// whole-body buffering path (used by ReadAsStringAsync and by HttpClient's ResponseContentRead
        /// completion option) was ever invoked.
        /// </summary>
        private class InstrumentedJsonContent : HttpContent
        {
            private readonly byte[] _data;

            public InstrumentedJsonContent(string json)
            {
                _data = Encoding.UTF8.GetBytes(json);
                Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            }

            public bool BufferingAttempted { get; private set; }
            public bool ContentDisposed { get; private set; }
            public int StreamsCreated { get; private set; }
            public ForwardOnlyStream LastStream { get; private set; }

            protected override Task<Stream> CreateContentReadStreamAsync()
            {
                StreamsCreated++;
                LastStream = new ForwardOnlyStream(_data);

                return Task.FromResult<Stream>(LastStream);
            }

            protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext context)
            {
                BufferingAttempted = true;

                return stream.WriteAsync(_data, 0, _data.Length);
            }

            protected override bool TryComputeLength(out long length)
            {
                length = _data.Length;

                return true;
            }

            protected override void Dispose(bool disposing)
            {
                ContentDisposed = true;

                base.Dispose(disposing);
            }
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
    }
}
