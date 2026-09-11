// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using NUnit.Framework;
using Shouldly;
using System;

namespace EdFi.Tools.ApiPublisher.Tests.Configuration
{
    /// <summary>
    /// Verifies the cursor paging options added for APIPUB-139: the DisableCursorPaging default, and the
    /// partition count's default (the page-streaming parallelism, capped at the API maximum) and validation.
    /// </summary>
    [TestFixture]
    public class OptionsCursorPagingTests
    {
        [Test]
        public void Cursor_paging_should_be_enabled_by_default()
        {
            new Options().DisableCursorPaging.ShouldBeFalse();
        }

        [Test]
        public void Partition_count_should_default_to_the_page_streaming_parallelism()
        {
            var options = new Options { MaxDegreeOfParallelismForStreamResourcePages = 7 };

            options.CursorPagingPartitionCount.ShouldBeNull();
            options.ResolvedCursorPagingPartitionCount.ShouldBe(7);
        }

        [Test]
        public void Default_partition_count_should_be_capped_at_the_api_maximum()
        {
            var options = new Options { MaxDegreeOfParallelismForStreamResourcePages = 300 };

            options.ResolvedCursorPagingPartitionCount.ShouldBe(Options.MaxCursorPagingPartitionCount);
            Options.MaxCursorPagingPartitionCount.ShouldBe(200);
        }

        [Test]
        public void Explicit_partition_count_should_be_used_as_is()
        {
            var options = new Options { MaxDegreeOfParallelismForStreamResourcePages = 5, CursorPagingPartitionCount = 12 };

            options.ResolvedCursorPagingPartitionCount.ShouldBe(12);
        }

        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(201)]
        public void Out_of_range_partition_count_should_be_rejected(int partitionCount)
        {
            var options = new Options { CursorPagingPartitionCount = partitionCount };

            var exception = Should.Throw<InvalidOperationException>(() => options.ResolvedCursorPagingPartitionCount);

            exception.Message.ShouldContain("1 to 200");
        }
    }
}
