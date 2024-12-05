// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Counting;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Handlers;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using Serilog;
using System.Threading.Tasks.Dataflow;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.MessageProducers;

public class EdFiApiChangeVersionPagingStreamResourcePageMessageProducer : IStreamResourcePageMessageProducer
{
    private readonly ISourceTotalCountProvider _sourceTotalCountProvider;
    private readonly ILogger _logger = Log.ForContext(typeof(EdFiApiLimitOffsetPagingStreamResourcePageMessageProducer));

    public EdFiApiChangeVersionPagingStreamResourcePageMessageProducer(ISourceTotalCountProvider sourceTotalCountProvider)
    {
        _sourceTotalCountProvider = sourceTotalCountProvider;
    }

    public async Task<IEnumerable<StreamResourcePageMessage<TProcessDataMessage>>> ProduceMessagesAsync<TProcessDataMessage>(
        StreamResourceMessage message,
        Options options,
        ITargetBlock<ErrorItemMessage> errorHandlingBlock,
        Func<StreamResourcePageMessage<TProcessDataMessage>, string, IEnumerable<TProcessDataMessage>> createProcessDataMessages,
        CancellationToken cancellationToken)
    {
        if (message.ChangeWindow?.MaxChangeVersion != default(long) && message.ChangeWindow?.MaxChangeVersion != null)
        {
            _logger.Information("{ResourceUrl}: Retrieving total count of items in change versions {MinChangeVersion} to {MaxChangeVersion}.",
                message.ResourceUrl, message.ChangeWindow.MinChangeVersion, message.ChangeWindow.MaxChangeVersion);
        }
        else
        {
            _logger.Information("{ResourceUrl}: Retrieving total count of items.", message.ResourceUrl);
        }

        int limit = message.PageSize;

        var pageMessages = new List<StreamResourcePageMessage<TProcessDataMessage>>();

        var noOfPartitions = Math.Ceiling((decimal)(message.ChangeWindow.MaxChangeVersion - message.ChangeWindow.MinChangeVersion)
                        / options.ChangeVersionPagingWindowSize);

        // There must be at least 1 partition.
        // APIPUB-84, Success ticket 2783
        if (noOfPartitions == 0)
        {
            noOfPartitions = 1;
        }

        int changeVersionWindow = 0;
        long changeVersionWindowStartValue = message.ChangeWindow.MinChangeVersion;
        long totalCount = 0;

        while (changeVersionWindow < noOfPartitions)
        {
            long changeVersionWindowEndValue = (changeVersionWindowStartValue > 0 ?
                changeVersionWindowStartValue - 1 : changeVersionWindowStartValue) + options.ChangeVersionPagingWindowSize;

            if (changeVersionWindowEndValue > message.ChangeWindow.MaxChangeVersion)
            {
                changeVersionWindowEndValue = message.ChangeWindow.MaxChangeVersion;
            }
            var changeWindow = new ChangeWindow
            {
                MinChangeVersion = changeVersionWindowStartValue,
                MaxChangeVersion = changeVersionWindowEndValue
            };
            changeVersionWindowStartValue = changeVersionWindowEndValue + 1;

            // Get total count of items in source resource for change window (if applicable)
            var (totalCountOnWindowSuccess, totalCountOnWindow) = await _sourceTotalCountProvider.TryGetTotalCountAsync(
                message.ResourceUrl,
                options,
                changeWindow,
                errorHandlingBlock,
                cancellationToken);

            if (!totalCountOnWindowSuccess)
            {
                // Allow processing to continue without performing additional work on this resource.
                return Enumerable.Empty<StreamResourcePageMessage<TProcessDataMessage>>();
            }

            totalCount += totalCountOnWindow;
            int offsetOnWindow = 0;
            while (offsetOnWindow < totalCountOnWindow)
            {
                var pageMessage = new StreamResourcePageMessage<TProcessDataMessage>
                {
                    // Resource-specific context
                    ResourceUrl = message.ResourceUrl,
                    PostAuthorizationFailureRetry = message.PostAuthorizationFailureRetry,

                    // Page-strategy specific context
                    Limit = limit,
                    Offset = offsetOnWindow,

                    // Global processing context                   
                    ChangeWindow = changeWindow,
                    CreateProcessDataMessages = createProcessDataMessages,

                    CancellationSource = message.CancellationSource,
                };

                pageMessages.Add(pageMessage);
                offsetOnWindow += limit;
            }
            changeVersionWindow++;

        }

        _logger.Information($"{message.ResourceUrl}: Total count = {totalCount}");

        // Flag the last page for special "continuation" processing
        if (pageMessages.Any())
        {
            // Page-strategy specific context
            pageMessages[pageMessages.Count - 1].IsFinalPage = true;
        }

        return pageMessages;
    }
}
