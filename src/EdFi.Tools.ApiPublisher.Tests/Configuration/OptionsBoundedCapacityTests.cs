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
    /// Verifies the boundary behavior of the processingBlockBoundedCapacity option introduced for APIPUB-112:
    /// the -1 rollback mode, the automatic capacity derivation, the explicit-value floor, the derived page and
    /// error publishing capacities, and the rejection of invalid values.
    /// </summary>
    [TestFixture]
    public class OptionsBoundedCapacityTests
    {
        private static Options CreateOptions(
            int boundedCapacity,
            int streamingPageSize = 100,
            int postItemDop = 15,
            int pagesDop = 5,
            int errorBatchSize = 25)
        {
            return new Options
            {
                ProcessingBlockBoundedCapacity = boundedCapacity,
                StreamingPageSize = streamingPageSize,
                MaxDegreeOfParallelismForPostResourceItem = postItemDop,
                MaxDegreeOfParallelismForStreamResourcePages = pagesDop,
                ErrorPublishingBatchSize = errorBatchSize,
            };
        }

        [Test]
        public void Minus_one_should_disable_all_resolved_bounds()
        {
            var options = CreateOptions(-1);

            options.ResolvedProcessingBlockBoundedCapacity.ShouldBe(-1);
            options.ResolvedStreamResourcePagesBlockBoundedCapacity.ShouldBe(-1);
            options.ResolvedErrorPublishingBoundedCapacity.ShouldBe(-1);
        }

        [Test]
        public void Automatic_capacity_should_use_page_size_when_it_dominates()
        {
            var options = CreateOptions(0, streamingPageSize: 500, postItemDop: 15);

            // max(500, 4 x 15)
            options.ResolvedProcessingBlockBoundedCapacity.ShouldBe(500);
        }

        [Test]
        public void Automatic_capacity_should_use_post_parallelism_when_it_dominates()
        {
            var options = CreateOptions(0, streamingPageSize: 10, postItemDop: 20);

            // max(10, 4 x 20)
            options.ResolvedProcessingBlockBoundedCapacity.ShouldBe(80);
        }

        [Test]
        public void Explicit_capacity_below_post_parallelism_should_be_floored_to_it()
        {
            var options = CreateOptions(5, postItemDop: 15);

            options.ResolvedProcessingBlockBoundedCapacity.ShouldBe(15);
        }

        [Test]
        public void Explicit_capacity_above_post_parallelism_should_be_used_verbatim()
        {
            var options = CreateOptions(1000, postItemDop: 15);

            options.ResolvedProcessingBlockBoundedCapacity.ShouldBe(1000);
        }

        [Test]
        public void Pages_capacity_should_use_page_equivalent_of_item_capacity_when_it_dominates()
        {
            var options = CreateOptions(5000, streamingPageSize: 100, postItemDop: 15, pagesDop: 5);

            // max(2 x 5, 5000 / 100)
            options.ResolvedStreamResourcePagesBlockBoundedCapacity.ShouldBe(50);
        }

        [Test]
        public void Pages_capacity_should_use_pages_parallelism_when_it_dominates()
        {
            var options = CreateOptions(0, streamingPageSize: 100, postItemDop: 15, pagesDop: 5);

            // item capacity resolves to max(100, 60) = 100 = 1 page; max(2 x 5, 1)
            options.ResolvedStreamResourcePagesBlockBoundedCapacity.ShouldBe(10);
        }

        [Test]
        public void Error_capacity_should_be_twice_the_error_publishing_batch_size()
        {
            var options = CreateOptions(0, errorBatchSize: 25);

            options.ResolvedErrorPublishingBoundedCapacity.ShouldBe(50);
        }

        [Test]
        public void Error_capacity_should_tolerate_a_degenerate_batch_size()
        {
            var options = CreateOptions(0, errorBatchSize: 0);

            // 2 x max(1, 0)
            options.ResolvedErrorPublishingBoundedCapacity.ShouldBe(2);
        }

        [Test]
        public void Invalid_capacity_should_round_trip_for_validation_and_resolved_properties_should_throw()
        {
            var options = CreateOptions(-2);

            // The raw value must survive configuration binding so CLI options validation can reject it
            options.ProcessingBlockBoundedCapacity.ShouldBe(-2);

            Should.Throw<InvalidOperationException>(() => options.ResolvedProcessingBlockBoundedCapacity)
                .Message.ShouldContain("-2");
            Should.Throw<InvalidOperationException>(() => options.ResolvedStreamResourcePagesBlockBoundedCapacity);
            Should.Throw<InvalidOperationException>(() => options.ResolvedErrorPublishingBoundedCapacity);
        }
    }
}
