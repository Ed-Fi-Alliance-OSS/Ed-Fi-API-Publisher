// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Paging;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using NUnit.Framework;
using Shouldly;
using System;
using System.Net;
using System.Net.Http;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// Verifies the offset/limit page-request strategy extracted from the source page read handler
    /// (see APIPUB-138): the query string, the log descriptions, and the "final page still full"
    /// continuation decision match the handler's previous hard-coded behavior.
    /// </summary>
    [TestFixture]
    public class OffsetPageRequestStrategyTests
    {
        private static StreamResourcePageMessage<object> CreateMessage(
            long? offset = 0,
            int? limit = 500,
            bool isFinalPage = false)
        {
            return new StreamResourcePageMessage<object>
            {
                ResourceUrl = "/ed-fi/students",
                Offset = offset,
                Limit = limit,
                IsFinalPage = isFinalPage,
            };
        }

        private static Options CreateOptions(bool useReversePaging = false)
        {
            var options = TestHelpers.GetOptions();
            options.UseReversePaging = useReversePaging;

            return options;
        }

        private static HttpResponseMessage OkResponse() => new HttpResponseMessage(HttpStatusCode.OK);

        [Test]
        public void Begin_should_require_an_offset_on_the_message()
        {
            var strategy = new OffsetPageRequestStrategy();

            var exception = Should.Throw<NullReferenceException>(
                () => strategy.Begin(CreateMessage(offset: null), CreateOptions()));

            exception.Message.ShouldBe("Offset is expected on resource page messages for the Ed-Fi ODS API.");
        }

        [Test]
        public void Begin_should_require_a_limit_on_the_message()
        {
            var strategy = new OffsetPageRequestStrategy();

            var exception = Should.Throw<NullReferenceException>(
                () => strategy.Begin(CreateMessage(limit: null), CreateOptions()));

            exception.Message.ShouldBe("Limit is expected on resource page messages for the Ed-Fi ODS API.");
        }

        [Test]
        public void First_request_should_use_the_offset_and_limit_from_the_message()
        {
            var sequence = new OffsetPageRequestStrategy().Begin(CreateMessage(offset: 1000, limit: 250), CreateOptions());

            sequence.BuildQueryString().ShouldBe("?offset=1000&limit=250");
        }

        [Test]
        public void Description_should_state_the_inclusive_item_range_of_the_current_request()
        {
            var sequence = new OffsetPageRequestStrategy().Begin(CreateMessage(offset: 1000, limit: 250), CreateOptions());

            sequence.Describe().ShouldBe("1000 to 1249");
        }

        [Test]
        public void Start_description_should_state_the_offset_of_the_current_request()
        {
            var sequence = new OffsetPageRequestStrategy().Begin(CreateMessage(offset: 1000, limit: 250), CreateOptions());

            sequence.DescribeStart().ShouldBe("offset 1000");
        }

        [Test]
        public void Full_final_page_should_advance_to_the_next_offset()
        {
            var sequence = new OffsetPageRequestStrategy().Begin(
                CreateMessage(offset: 1000, limit: 250, isFinalPage: true),
                CreateOptions());

            sequence.TryAdvance(OkResponse(), topLevelItemCount: 250).ShouldBeTrue();

            sequence.BuildQueryString().ShouldBe("?offset=1250&limit=250");
            sequence.Describe().ShouldBe("1250 to 1499");
            sequence.DescribeStart().ShouldBe("offset 1250");
        }

        [Test]
        public void Advancing_should_keep_going_while_each_final_page_stays_full()
        {
            var sequence = new OffsetPageRequestStrategy().Begin(
                CreateMessage(offset: 0, limit: 100, isFinalPage: true),
                CreateOptions());

            sequence.TryAdvance(OkResponse(), topLevelItemCount: 100).ShouldBeTrue();
            sequence.TryAdvance(OkResponse(), topLevelItemCount: 100).ShouldBeTrue();
            sequence.BuildQueryString().ShouldBe("?offset=200&limit=100");

            sequence.TryAdvance(OkResponse(), topLevelItemCount: 37).ShouldBeFalse();
        }

        [Test]
        public void Short_final_page_should_not_advance()
        {
            var sequence = new OffsetPageRequestStrategy().Begin(
                CreateMessage(offset: 1000, limit: 250, isFinalPage: true),
                CreateOptions());

            sequence.TryAdvance(OkResponse(), topLevelItemCount: 249).ShouldBeFalse();

            // The current request is unchanged
            sequence.BuildQueryString().ShouldBe("?offset=1000&limit=250");
        }

        [Test]
        public void Full_page_that_is_not_the_final_page_should_not_advance()
        {
            var sequence = new OffsetPageRequestStrategy().Begin(
                CreateMessage(offset: 1000, limit: 250, isFinalPage: false),
                CreateOptions());

            sequence.TryAdvance(OkResponse(), topLevelItemCount: 250).ShouldBeFalse();
        }

        [Test]
        public void Final_page_without_a_reported_item_count_should_not_advance()
        {
            // No count is reported when item creation stopped early alongside cancellation
            var sequence = new OffsetPageRequestStrategy().Begin(
                CreateMessage(offset: 1000, limit: 250, isFinalPage: true),
                CreateOptions());

            sequence.TryAdvance(OkResponse(), topLevelItemCount: null).ShouldBeFalse();
        }

        [Test]
        public void Full_final_page_should_not_advance_when_reverse_paging_is_in_use()
        {
            var sequence = new OffsetPageRequestStrategy().Begin(
                CreateMessage(offset: 1000, limit: 250, isFinalPage: true),
                CreateOptions(useReversePaging: true));

            sequence.TryAdvance(OkResponse(), topLevelItemCount: 250).ShouldBeFalse();
        }
    }
}
