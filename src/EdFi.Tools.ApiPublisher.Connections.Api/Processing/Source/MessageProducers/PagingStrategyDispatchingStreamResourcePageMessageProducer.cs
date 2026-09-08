// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Paging;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing.Blocks;
using EdFi.Tools.ApiPublisher.Core.Processing.Handlers;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using Serilog;
using System.Threading.Tasks.Dataflow;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.MessageProducers;

/// <summary>
/// Routes each resource to the offset-family producer (limit/offset, change-version or reverse paging, chosen at
/// registration time exactly as before) or to the cursor paging producer, per <see cref="ISourcePagingStrategyResolver" />.
/// When the partitions request fails for a resource, that resource falls back to offset paging (see APIPUB-139).
/// </summary>
public class PagingStrategyDispatchingStreamResourcePageMessageProducer : IStreamResourcePageMessageProducer
{
    private readonly ISourcePagingStrategyResolver _pagingStrategyResolver;
    private readonly IStreamResourcePageMessageProducer _offsetPagingProducer;
    private readonly EdFiApiCursorPagingStreamResourcePageMessageProducer _cursorPagingProducer;

    private readonly ILogger _logger = Log.ForContext(typeof(PagingStrategyDispatchingStreamResourcePageMessageProducer));

    public PagingStrategyDispatchingStreamResourcePageMessageProducer(
        ISourcePagingStrategyResolver pagingStrategyResolver,
        IStreamResourcePageMessageProducer offsetPagingProducer,
        EdFiApiCursorPagingStreamResourcePageMessageProducer cursorPagingProducer)
    {
        _pagingStrategyResolver = pagingStrategyResolver ?? throw new ArgumentNullException(nameof(pagingStrategyResolver));
        _offsetPagingProducer = offsetPagingProducer ?? throw new ArgumentNullException(nameof(offsetPagingProducer));
        _cursorPagingProducer = cursorPagingProducer ?? throw new ArgumentNullException(nameof(cursorPagingProducer));
    }

    public async Task<IEnumerable<StreamResourcePageMessage<TProcessDataMessage>>> ProduceMessagesAsync<TProcessDataMessage>(
        StreamResourceMessage message,
        Options options,
        ITargetBlock<ErrorItemMessage> errorHandlingBlock,
        Func<StreamResourcePageMessage<TProcessDataMessage>, TextReader, Action<int>, IEnumerable<TProcessDataMessage>> createProcessDataMessages,
        CancellationToken cancellationToken)
    {
        var strategy = await _pagingStrategyResolver.ResolveAsync(message.ResourceUrl, options).ConfigureAwait(false);

        if (strategy == SourcePagingStrategy.Cursor)
        {
            var (success, messages) = await _cursorPagingProducer.TryProduceMessagesAsync(
                message, options, errorHandlingBlock, createProcessDataMessages, cancellationToken).ConfigureAwait(false);

            if (success)
            {
                return messages;
            }

            _logger.Debug("{ResourceUrl}: Cursor paging unavailable for this resource; producing offset/limit pages instead.", message.ResourceUrl);
        }

        return await _offsetPagingProducer.ProduceMessagesAsync(
            message, options, errorHandlingBlock, createProcessDataMessages, cancellationToken).ConfigureAwait(false);
    }
}
