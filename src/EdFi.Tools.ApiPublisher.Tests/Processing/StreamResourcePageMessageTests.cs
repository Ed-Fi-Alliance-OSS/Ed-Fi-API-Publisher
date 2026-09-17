// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Messages;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using NUnit.Framework;
using Shouldly;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// APIPUB-102: the page locator is metadata only (page bounds and change window) so that an item-level
    /// error can tell the operator where in the source the offending document lives without exposing any
    /// document content.
    /// </summary>
    [TestFixture]
    public class StreamResourcePageMessageTests
    {
        [Test]
        public void DescribeSourcePage_should_include_offset_limit_and_change_window_when_present()
        {
            var message = new StreamResourcePageMessage<PostItemMessage>
            {
                Offset = 1000,
                Limit = 500,
                ChangeWindow = new ChangeWindow { MinChangeVersion = 100, MaxChangeVersion = 200 },
            };

            message.DescribeSourcePage().ShouldBe("offset 1000, limit 500, change versions 100 to 200");
        }

        [Test]
        public void DescribeSourcePage_should_include_partition_bounds_when_present()
        {
            var message = new StreamResourcePageMessage<PostItemMessage>
            {
                Offset = 0,
                Limit = 50,
                PartitionFrom = "2024-01-01",
                PartitionUntil = "2024-02-01",
            };

            message.DescribeSourcePage().ShouldBe("offset 0, limit 50, partition from 2024-01-01 until 2024-02-01");
        }

        [Test]
        public void DescribeSourcePage_should_report_an_unknown_page_when_no_paging_context_is_available()
        {
            var message = new StreamResourcePageMessage<PostItemMessage>();

            message.DescribeSourcePage().ShouldBe("unknown page");
        }

        [Test]
        public void DescribeSourcePage_should_include_cursor_paging_context_when_present()
        {
            var message = new StreamResourcePageMessage<PostItemMessage>
            {
                PageToken = "MTIzLDQ1Ng",
                PageSize = 500,
                PartitionIndex = 2,
                ChangeWindow = new ChangeWindow { MinChangeVersion = 100, MaxChangeVersion = 200 },
            };

            message.DescribeSourcePage().ShouldBe("partition 2, page token MTIzLDQ1Ng, page size 500, change versions 100 to 200");
        }

        [Test]
        public void DescribeSourcePage_should_include_the_partition_page_number_when_present()
        {
            var message = new StreamResourcePageMessage<PostItemMessage>
            {
                PageToken = "NEXT",
                PageSize = 500,
                PartitionIndex = 2,
                PartitionPageNumber = 3,
            };

            message.DescribeSourcePage().ShouldBe("partition 2, page 3, page token NEXT, page size 500");
        }

        /// <summary>
        /// APIPUB-142: the page's identity, taken alongside the locator so that a checkpoint can name the
        /// page a document came from after the partition walk has moved the message on.
        /// </summary>
        [Test]
        public void CaptureSourcePageReference_should_take_the_cursor_page_identity()
        {
            var message = new StreamResourcePageMessage<PostItemMessage>
            {
                ResourceUrl = "/ed-fi/students",
                IsAuthorizationRetryPass = true,
                PageToken = "NEXT",
                PageSize = 500,
                PartitionIndex = 2,
                PartitionPageNumber = 3,
            };

            message.CaptureSourcePageReference()
                .ShouldBe(new SourcePageReference("/ed-fi/students", true, 2, 3, "NEXT"));
        }

        [Test]
        public void CaptureSourcePageReference_should_be_null_for_an_offset_paged_page()
        {
            var message = new StreamResourcePageMessage<PostItemMessage>
            {
                ResourceUrl = "/ed-fi/students/deletes",
                Offset = 1000,
                Limit = 500,
            };

            message.CaptureSourcePageReference().ShouldBeNull();
        }
    }
}
