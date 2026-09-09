// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.MessageHandlers;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Paging;
using FakeItEasy;
using NUnit.Framework;
using Shouldly;
using System;

namespace EdFi.Tools.ApiPublisher.Tests.Processing
{
    /// <summary>
    /// The source page read handler fails fast on a missing required dependency instead of surfacing a
    /// NullReferenceException on the first page read (see APIPUB-138).
    /// </summary>
    [TestFixture]
    public class EdFiApiStreamResourcePageMessageHandlerConstructionTests
    {
        [Test]
        public void Should_require_a_source_api_client_provider()
        {
            var exception = Should.Throw<ArgumentNullException>(
                () => new EdFiApiStreamResourcePageMessageHandler(null, new OffsetPageRequestStrategy()));

            exception.ParamName.ShouldBe("sourceEdFiApiClientProvider");
        }

        [Test]
        public void Should_require_a_page_request_strategy()
        {
            var exception = Should.Throw<ArgumentNullException>(
                () => new EdFiApiStreamResourcePageMessageHandler(A.Fake<ISourceEdFiApiClientProvider>(), null));

            exception.ParamName.ShouldBe("pageRequestStrategy");
        }

        [Test]
        public void Rate_limiter_should_remain_optional()
        {
            Should.NotThrow(
                () => new EdFiApiStreamResourcePageMessageHandler(
                    A.Fake<ISourceEdFiApiClientProvider>(),
                    new OffsetPageRequestStrategy(),
                    rateLimiter: null));
        }
    }
}
