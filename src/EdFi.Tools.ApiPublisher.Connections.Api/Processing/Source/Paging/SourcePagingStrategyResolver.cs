// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Capabilities;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing;
using Serilog;
using System.Collections.Concurrent;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Paging;

/// <summary>
/// Resolves the paging strategy per resource (see APIPUB-139): offset when cursor paging is disabled or when the
/// change-version / reverse paging workarounds are in use; offset for the deletes and keyChanges child resources
/// (7.3 exposes no /partitions under them and silently ignores pageToken there); otherwise cursor paging when the
/// source supports it. The decision is memoized per resource URL and logged once.
/// </summary>
public class SourcePagingStrategyResolver : ISourcePagingStrategyResolver
{
    private readonly ISourceCapabilities _sourceCapabilities;
    private readonly ConcurrentDictionary<string, Lazy<Task<SourcePagingStrategy>>> _strategyByResourceUrl = new();

    private readonly ILogger _logger = Log.ForContext(typeof(SourcePagingStrategyResolver));

    public SourcePagingStrategyResolver(ISourceCapabilities sourceCapabilities)
    {
        _sourceCapabilities = sourceCapabilities ?? throw new ArgumentNullException(nameof(sourceCapabilities));
    }

    public Task<SourcePagingStrategy> ResolveAsync(string resourceUrl, Options options)
    {
        ArgumentNullException.ThrowIfNull(resourceUrl);
        ArgumentNullException.ThrowIfNull(options);

        return _strategyByResourceUrl
            .GetOrAdd(resourceUrl, url => new Lazy<Task<SourcePagingStrategy>>(() => ResolveUncachedAsync(url, options)))
            .Value;
    }

    private async Task<SourcePagingStrategy> ResolveUncachedAsync(string resourceUrl, Options options)
    {
        var strategy = await DetermineStrategyAsync(resourceUrl, options).ConfigureAwait(false);

        _logger.Information("{ResourceUrl}: using {Strategy} paging", resourceUrl, strategy);

        return strategy;
    }

    private async Task<SourcePagingStrategy> DetermineStrategyAsync(string resourceUrl, Options options)
    {
        if (options.DisableCursorPaging || options.UseChangeVersionPaging || options.UseReversePaging)
        {
            return SourcePagingStrategy.Offset;
        }

        if (resourceUrl.EndsWith(EdFiApiConstants.DeletesPathSuffix, StringComparison.OrdinalIgnoreCase)
            || resourceUrl.EndsWith(EdFiApiConstants.KeyChangesPathSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return SourcePagingStrategy.Offset;
        }

        return await _sourceCapabilities.SupportsCursorPagingAsync(resourceUrl).ConfigureAwait(false)
            ? SourcePagingStrategy.Cursor
            : SourcePagingStrategy.Offset;
    }
}
