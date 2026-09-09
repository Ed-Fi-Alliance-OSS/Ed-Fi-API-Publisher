// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Paging;

/// <summary>
/// Selects the page-request strategy per message: cursor paging when the producer put a <c>pageToken</c> on the
/// message, offset/limit paging otherwise (see APIPUB-139). The read handler therefore stays unaware of how the
/// strategy for a resource was resolved.
/// </summary>
public class PageRequestStrategyDispatcher : IPageRequestStrategy
{
    private readonly OffsetPageRequestStrategy _offsetPageRequestStrategy;
    private readonly CursorPageRequestStrategy _cursorPageRequestStrategy;

    public PageRequestStrategyDispatcher(
        OffsetPageRequestStrategy offsetPageRequestStrategy,
        CursorPageRequestStrategy cursorPageRequestStrategy)
    {
        _offsetPageRequestStrategy = offsetPageRequestStrategy ?? throw new ArgumentNullException(nameof(offsetPageRequestStrategy));
        _cursorPageRequestStrategy = cursorPageRequestStrategy ?? throw new ArgumentNullException(nameof(cursorPageRequestStrategy));
    }

    public IPageRequestSequence Begin<TProcessDataMessage>(StreamResourcePageMessage<TProcessDataMessage> message, Options options)
        => message.PageToken is not null
            ? _cursorPageRequestStrategy.Begin(message, options)
            : _offsetPageRequestStrategy.Begin(message, options);
}
