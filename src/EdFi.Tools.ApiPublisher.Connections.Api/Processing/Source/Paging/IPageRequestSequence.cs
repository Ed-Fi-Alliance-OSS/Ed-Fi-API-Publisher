// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Serilog;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Paging;

/// <summary>
/// The sequence of GET requests that satisfy one page message, as produced by an
/// <see cref="IPageRequestStrategy" />. The sequence is positioned on the current request; a successful
/// page read is reported through <see cref="TryAdvance" />, which decides whether another request follows.
/// </summary>
public interface IPageRequestSequence
{
    /// <summary>
    /// Builds the paging portion of the query string for the current request, starting with '?'
    /// (the handler appends the change window parameters).
    /// </summary>
    string BuildQueryString();

    /// <summary>
    /// Describes the items addressed by the current request, completing the phrase "page items {0}"
    /// (e.g. "0 to 499").
    /// </summary>
    string Describe();

    /// <summary>
    /// Describes where the current request starts, completing the phrase "starting at {0}"
    /// (e.g. "offset 0").
    /// </summary>
    string DescribeStart();

    /// <summary>
    /// Returns the supplied logger enriched with the machine-readable paging values of the current request
    /// (e.g. <c>Offset</c> and <c>Limit</c>), so structured sinks keep the values that <see cref="Describe" />
    /// and <see cref="DescribeStart" /> summarize for people.
    /// </summary>
    ILogger EnrichLogger(ILogger logger);

    /// <summary>
    /// Reports a successfully read page and decides whether another request follows for this page message,
    /// advancing the sequence to it when so.
    /// </summary>
    /// <param name="response">The response whose body has just been read.</param>
    /// <param name="topLevelItemCount">
    /// The number of top-level items in the page, or <b>null</b> when no count was reported (item creation
    /// stopped early alongside cancellation).
    /// </param>
    /// <returns><b>true</b> if the sequence advanced to another request; otherwise <b>false</b>.</returns>
    bool TryAdvance(HttpResponseMessage response, int? topLevelItemCount);
}
