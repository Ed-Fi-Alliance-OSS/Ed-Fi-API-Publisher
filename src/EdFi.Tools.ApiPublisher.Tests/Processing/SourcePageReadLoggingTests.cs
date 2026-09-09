// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.MessageHandlers;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Paging;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Messages;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using FakeItEasy;
using NUnit.Framework;
using Serilog.Events;
using Serilog.Sinks.TestCorrelator;
using Shouldly;
using System;
using System.Collections.Generic;
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
    /// Pins the operator-facing log output of the source page read handler after the paging seam was
    /// extracted (see APIPUB-138): the page descriptions render unquoted, exactly as the offset/limit
    /// numbers did before (the resource URL has always rendered quoted, as a string scalar), and the
    /// numeric paging values stay available to structured sinks.
    /// </summary>
    [TestFixture]
    public class SourcePageReadLoggingTests
    {
        private static (EdFiApiStreamResourcePageMessageHandler handler, IFakeHttpRequestHandler fakeRequestHandler) CreateHandler()
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

            return (new EdFiApiStreamResourcePageMessageHandler(sourceClientProvider, new OffsetPageRequestStrategy()), fakeRequestHandler);
        }

        private static void SetupPageGet(IFakeHttpRequestHandler fakeRequestHandler, Func<HttpResponseMessage> createResponse)
        {
            A.CallTo(
                    () => fakeRequestHandler.Get(
                        A<string>.Ignored,
                        A<HttpRequestMessage>.That.Matches(msg => msg.RequestUri.LocalPath == "/data/v3/ed-fi/students")))
                .ReturnsLazily(createResponse);
        }

        private static StreamResourcePageMessage<PostItemMessage> CreatePageMessage()
        {
            return new StreamResourcePageMessage<PostItemMessage>
            {
                ResourceUrl = "/ed-fi/students",
                Offset = 0,
                Limit = 50,
                IsFinalPage = false,
                CancellationSource = new CancellationTokenSource(),
                CreateProcessDataMessages = (_, _, _) => Enumerable.Empty<PostItemMessage>(),
            };
        }

        private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) =>
            new HttpResponseMessage(statusCode) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private static LogEvent Single(IEnumerable<LogEvent> events, LogEventLevel level, string fragment)
        {
            var matches = events.Where(e => e.Level == level && e.MessageTemplate.Text.Contains(fragment)).ToArray();

            matches.Length.ShouldBe(1, $"Expected exactly one {level} event containing '{fragment}'.");

            return matches[0];
        }

        private static void ShouldCarryPagingValues(LogEvent logEvent, long offset, int limit)
        {
            logEvent.Properties["Offset"].ShouldBe(new ScalarValue(offset));
            logEvent.Properties["Limit"].ShouldBe(new ScalarValue(limit));
        }

        [Test]
        public async Task Page_descriptions_should_render_unquoted_and_carry_the_numeric_paging_values()
        {
            TestHelpers.InitializeLogging();

            var (handler, fakeRequestHandler) = CreateHandler();

            int attempts = 0;

            SetupPageGet(
                fakeRequestHandler,
                () => Interlocked.Increment(ref attempts) == 1
                    ? JsonResponse(HttpStatusCode.ServiceUnavailable, @"{""message"":""temporarily unavailable""}")
                    : JsonResponse(HttpStatusCode.OK, "[]"));

            using (TestCorrelator.CreateContext())
            {
                await handler.HandleStreamResourcePageAsync(CreatePageMessage(), TestHelpers.GetOptions(), new BufferBlock<ErrorItemMessage>()).ToArrayAsync();

                var events = TestCorrelator.GetLogEventsFromCurrentContext().ToArray();

                var retrieving = Single(events, LogEventLevel.Debug, "Retrieving page items");
                retrieving.RenderMessage().ShouldBe("\"/ed-fi/students\": Retrieving page items 0 to 49.");
                ShouldCarryPagingValues(retrieving, offset: 0, limit: 50);

                var retrying = Single(events, LogEventLevel.Warning, "Retrying GET page items");
                retrying.RenderMessage().ShouldStartWith(
                    "\"/ed-fi/students\": Retrying GET page items 0 to 49 from source failed with status 'ServiceUnavailable'. Retrying...");
                ShouldCarryPagingValues(retrying, offset: 0, limit: 50);

                var attempt = Single(events, LogEventLevel.Debug, "from source attempt");
                attempt.RenderMessage().ShouldBe("\"/ed-fi/students\": GET page items 0 to 49 from source attempt #2.");

                var succeeded = Single(events, LogEventLevel.Information, "attempt #{Attempts} returned");
                succeeded.RenderMessage().ShouldBe("\"/ed-fi/students\": GET page items 0 to 49 attempt #2 returned OK.");
                ShouldCarryPagingValues(succeeded, offset: 0, limit: 50);
            }
        }

        [Test]
        public async Task Non_transient_failure_should_name_the_page_items_that_were_not_read()
        {
            TestHelpers.InitializeLogging();

            var (handler, fakeRequestHandler) = CreateHandler();

            SetupPageGet(fakeRequestHandler, () => JsonResponse(HttpStatusCode.Forbidden, @"{""message"":""forbidden""}"));

            using (TestCorrelator.CreateContext())
            {
                var errorBlock = new BufferBlock<ErrorItemMessage>();

                await handler.HandleStreamResourcePageAsync(CreatePageMessage(), TestHelpers.GetOptions(), errorBlock).ToArrayAsync();

                errorBlock.Count.ShouldBe(1);

                var failed = Single(TestCorrelator.GetLogEventsFromCurrentContext(), LogEventLevel.Error, "GET page items");
                failed.RenderMessage().ShouldBe("\"/ed-fi/students\": GET page items 0 to 49 failed with response status 'Forbidden'.");
                ShouldCarryPagingValues(failed, offset: 0, limit: 50);
            }
        }

        [Test]
        public async Task Cancellation_before_the_request_should_render_the_page_start_unquoted()
        {
            TestHelpers.InitializeLogging();

            var (handler, _) = CreateHandler();

            var message = CreatePageMessage();
            message.CancellationSource.Cancel();

            using (TestCorrelator.CreateContext())
            {
                var itemMessages = await handler.HandleStreamResourcePageAsync(message, TestHelpers.GetOptions(), new BufferBlock<ErrorItemMessage>()).ToArrayAsync();

                itemMessages.ShouldBeEmpty();

                var cancelled = Single(TestCorrelator.GetLogEventsFromCurrentContext(), LogEventLevel.Debug, "Cancellation requested");
                cancelled.RenderMessage().ShouldBe(
                    "\"/ed-fi/students\": Cancellation requested while processing page of source items starting at offset 0.");
                ShouldCarryPagingValues(cancelled, offset: 0, limit: 50);
            }
        }
    }
}
