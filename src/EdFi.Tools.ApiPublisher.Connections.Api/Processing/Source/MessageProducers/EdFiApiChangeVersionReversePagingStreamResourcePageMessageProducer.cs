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

namespace EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.MessageProducers;

public class EdFiApiChangeVersionReversePagingStreamResourcePageMessageProducer : IStreamResourcePageMessageProducer
{
    private readonly ISourceTotalCountProvider _sourceTotalCountProvider;
    private readonly ILogger _logger = Log.ForContext(typeof(EdFiApiLimitOffsetPagingStreamResourcePageMessageProducer));

    public EdFiApiChangeVersionReversePagingStreamResourcePageMessageProducer(ISourceTotalCountProvider sourceTotalCountProvider)
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
            _logger.Information(
                $"{message.ResourceUrl}: Retrieving total count of items in change versions {message.ChangeWindow.MinChangeVersion} to {message.ChangeWindow.MaxChangeVersion}.");
        }
        else
        {
            _logger.Information($"{message.ResourceUrl}: Retrieving total count of items.");
        }

        // Get total count of items in source resource for change window (if applicable)
        var (totalCountSuccess, totalCount) = await _sourceTotalCountProvider.TryGetTotalCountAsync(
            message.ResourceUrl,
            options,
            message.ChangeWindow,
            errorHandlingBlock,
            cancellationToken);

        if (!totalCountSuccess)
        {
            // Allow processing to continue without performing additional work on this resource.
            return Enumerable.Empty<StreamResourcePageMessage<TProcessDataMessage>>();
        }

        _logger.Information($"{message.ResourceUrl}: Total count = {totalCount}");

        int limit = message.PageSize;

        var pageMessages = new List<StreamResourcePageMessage<TProcessDataMessage>>();

        if (totalCount > 0)
        {
            var noOfPartitions = Math.Ceiling((decimal)(message.ChangeWindow.MaxChangeVersion - message.ChangeWindow.MinChangeVersion)
                            / options.ChangeVersionPagingWindowSize);

            int changeVersionWindow = 0;
            long changeVersionWindowStartValue = message.ChangeWindow.MinChangeVersion;

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
                    continue;
                }

                bool isLastOne = false;
                long offsetOnWindow = totalCountOnWindow - limit;
                if (offsetOnWindow < 0)
                {
                    offsetOnWindow = 0;
                    isLastOne = true;
                }

                int limitOnWindow = totalCountOnWindow < limit ? (int)totalCountOnWindow : limit;
                while ((offsetOnWindow >= 0 || isLastOne == true) && totalCountOnWindow > 0 && limitOnWindow > 0)
                {
                    var pageMessage = new StreamResourcePageMessage<TProcessDataMessage>
                    {
                        // Resource-specific context
                        ResourceUrl = message.ResourceUrl,
                        PostAuthorizationFailureRetry = message.PostAuthorizationFailureRetry,

                        // Page-strategy specific context
                        Limit = limitOnWindow,
                        Offset = offsetOnWindow,

                        // Global processing context                   
                        ChangeWindow = changeWindow,
                        CreateProcessDataMessages = createProcessDataMessages,

                        CancellationSource = message.CancellationSource,
                    };

                    pageMessages.Add(pageMessage);
                    offsetOnWindow -= limit;
                    if (isLastOne)
                        break;
                    if (offsetOnWindow < 0)
                    {
                        limitOnWindow = limit + (int)offsetOnWindow;
                        offsetOnWindow = 0;
                        isLastOne = true;
                    }
                }
                changeVersionWindow++;

            }
        }

        // Flag the last page for special "continuation" processing
        if (pageMessages.Any())
        {
            // Page-strategy specific context
            pageMessages.Last().IsFinalPage = true;
        }

        return pageMessages;
    }
}
