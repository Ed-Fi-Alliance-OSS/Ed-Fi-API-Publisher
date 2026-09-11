// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Paging;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using EdFi.Tools.ApiPublisher.Tests.Helpers;
using NUnit.Framework;
using Shouldly;
using System;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// Verifies that the handler-side strategy dispatcher (APIPUB-139) picks cursor paging for messages that carry a
    /// page token and offset/limit paging otherwise, so the read handler needs no knowledge of the resolution.
    /// </summary>
    [TestFixture]
    public class PageRequestStrategyDispatcherTests
    {
        private static PageRequestStrategyDispatcher Create() => new(new OffsetPageRequestStrategy(), new CursorPageRequestStrategy());

        [Test]
        public void Message_with_a_page_token_should_use_cursor_paging()
        {
            var sequence = Create().Begin(new StreamResourcePageMessage<object> { ResourceUrl = "/ed-fi/students", PageToken = "abc", PageSize = 50 }, TestHelpers.GetOptions());

            sequence.BuildQueryString().ShouldBe("?pageToken=abc&pageSize=50");
        }

        [Test]
        public void Message_without_a_page_token_should_use_offset_paging()
        {
            var sequence = Create().Begin(new StreamResourcePageMessage<object> { ResourceUrl = "/ed-fi/students", Offset = 100, Limit = 50 }, TestHelpers.GetOptions());

            sequence.BuildQueryString().ShouldBe("?offset=100&limit=50");
        }

        [Test]
        public void Constructor_should_require_both_strategies()
        {
            Should.Throw<ArgumentNullException>(() => new PageRequestStrategyDispatcher(null, new CursorPageRequestStrategy())).ParamName.ShouldBe("offsetPageRequestStrategy");
            Should.Throw<ArgumentNullException>(() => new PageRequestStrategyDispatcher(new OffsetPageRequestStrategy(), null)).ParamName.ShouldBe("cursorPageRequestStrategy");
        }
    }
}
