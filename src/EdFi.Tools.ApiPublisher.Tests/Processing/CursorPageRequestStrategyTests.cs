// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Paging;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using NUnit.Framework;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.TestCorrelator;
using Shouldly;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// Verifies the cursor page-request strategy (APIPUB-139): pageToken/pageSize query string only, the token
    /// passed verbatim, advancing while Next-Page-Token is present (regardless of page fullness), stopping when it is
    /// absent, and refusing to loop on a source that echoes the same token back.
    /// </summary>
    [TestFixture]
    public class CursorPageRequestStrategyTests
    {
        private static StreamResourcePageMessage<object> CreateMessage(string pageToken = "MTIzLDQ1Ng", int? pageSize = 500, int? partitionIndex = 2) =>
            new() { ResourceUrl = "/ed-fi/students", PageToken = pageToken, PageSize = pageSize, PartitionIndex = partitionIndex };

        private static HttpResponseMessage Response(string nextPageToken = null)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);

            if (nextPageToken is not null)
            {
                response.Headers.Add(CursorPageRequestStrategy.NextPageTokenHeader, nextPageToken);
            }

            return response;
        }

        [Test]
        public void Begin_should_require_a_page_token_on_the_message()
        {
            Should.Throw<NullReferenceException>(() => new CursorPageRequestStrategy().Begin(CreateMessage(pageToken: null), TestHelpers.GetOptions()))
                .Message.ShouldBe("PageToken is expected on cursor-paged resource page messages for the Ed-Fi ODS API.");
        }

        [Test]
        public void Begin_should_require_a_page_size_on_the_message()
        {
            Should.Throw<NullReferenceException>(() => new CursorPageRequestStrategy().Begin(CreateMessage(pageSize: null), TestHelpers.GetOptions()))
                .Message.ShouldBe("PageSize is expected on cursor-paged resource page messages for the Ed-Fi ODS API.");
        }

        [Test]
        public void First_request_should_send_only_pageToken_and_pageSize_with_the_token_verbatim()
        {
            var sequence = new CursorPageRequestStrategy().Begin(CreateMessage(), TestHelpers.GetOptions());

            sequence.BuildQueryString().ShouldBe("?pageToken=MTIzLDQ1Ng&pageSize=500");
            sequence.BuildQueryString().ShouldNotContain("offset");
            sequence.BuildQueryString().ShouldNotContain("limit");
            sequence.BuildQueryString().ShouldNotContain("totalCount");
        }

        [Test]
        public void Descriptions_should_name_the_partition_and_page()
        {
            var sequence = new CursorPageRequestStrategy().Begin(CreateMessage(), TestHelpers.GetOptions());

            sequence.Describe().ShouldBe("of partition 2, page 1");
            sequence.DescribeStart().ShouldBe("partition 2, page 1");
        }

        [Test]
        public void Next_page_token_should_advance_to_the_next_page_even_when_the_page_was_short()
        {
            var sequence = new CursorPageRequestStrategy().Begin(CreateMessage(), TestHelpers.GetOptions());

            sequence.TryAdvance(Response("NEXT"), topLevelItemCount: 3).ShouldBeTrue();

            sequence.BuildQueryString().ShouldBe("?pageToken=NEXT&pageSize=500");
            sequence.Describe().ShouldBe("of partition 2, page 2");
        }

        [Test]
        public void Missing_next_page_token_should_end_the_walk()
        {
            var sequence = new CursorPageRequestStrategy().Begin(CreateMessage(), TestHelpers.GetOptions());

            sequence.TryAdvance(Response(), topLevelItemCount: 500).ShouldBeFalse();
            sequence.BuildQueryString().ShouldBe("?pageToken=MTIzLDQ1Ng&pageSize=500");
        }

        [Test]
        public void Echoed_token_should_end_the_walk_with_a_warning()
        {
            TestHelpers.InitializeLogging();
            var sequence = new CursorPageRequestStrategy().Begin(CreateMessage(), TestHelpers.GetOptions());

            using (TestCorrelator.CreateContext())
            {
                sequence.TryAdvance(Response("MTIzLDQ1Ng"), topLevelItemCount: 1).ShouldBeFalse();

                TestCorrelator.GetLogEventsFromCurrentContext()
                    .Count(e => e.Level == LogEventLevel.Warning && e.MessageTemplate.Text.Contains("same"))
                    .ShouldBe(1);
            }
        }

        [Test]
        public void Enriched_logger_should_carry_the_token_page_size_partition_and_page_number()
        {
            var logger = new LoggerConfiguration().WriteTo.TestCorrelator().CreateLogger();
            var sequence = new CursorPageRequestStrategy().Begin(CreateMessage(), TestHelpers.GetOptions());

            using (TestCorrelator.CreateContext())
            {
                sequence.EnrichLogger(logger).Information("first");
                sequence.TryAdvance(Response("NEXT"), 500).ShouldBeTrue();
                sequence.EnrichLogger(logger).Information("second");

                var events = TestCorrelator.GetLogEventsFromCurrentContext().ToArray();
                var first = events.Single(e => e.MessageTemplate.Text == "first");
                var second = events.Single(e => e.MessageTemplate.Text == "second");

                first.Properties["PageToken"].ShouldBe(new ScalarValue("MTIzLDQ1Ng"));
                first.Properties["PageSize"].ShouldBe(new ScalarValue(500));
                first.Properties["PartitionIndex"].ShouldBe(new ScalarValue(2));
                first.Properties["PartitionPage"].ShouldBe(new ScalarValue(1));
                second.Properties["PageToken"].ShouldBe(new ScalarValue("NEXT"));
                second.Properties["PartitionPage"].ShouldBe(new ScalarValue(2));
            }
        }
    }
}
