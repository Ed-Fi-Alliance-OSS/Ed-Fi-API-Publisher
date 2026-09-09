// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Paging;

/// <summary>
/// Defines how the source page read handler addresses the successive GET requests that satisfy one
/// <see cref="StreamResourcePageMessage{TProcessDataMessage}" /> against the source Ed-Fi ODS API
/// (e.g. offset/limit paging, or cursor paging). The handler owns everything else about the request:
/// the streamed body read, retries, rate limiting, disposal and error publishing.
/// </summary>
public interface IPageRequestStrategy
{
    /// <summary>
    /// Starts the sequence of page requests for the supplied page message.
    /// </summary>
    /// <param name="message">The page message being handled.</param>
    /// <param name="options">The processing options for the current run.</param>
    /// <returns>The (stateful) sequence of requests for the page message, positioned on its first request.</returns>
    IPageRequestSequence Begin<TProcessDataMessage>(StreamResourcePageMessage<TProcessDataMessage> message, Options options);
}
