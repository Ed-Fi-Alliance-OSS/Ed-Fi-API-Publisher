// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Counting;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using log4net;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Handlers;

public class EdFiOdsApiLimitOffsetPagingStreamResourcePageMessageProducer : IStreamResourcePageMessageProducer
{
    private readonly IEdFiDataSourceTotalCountProvider _edFiDataSourceTotalCountProvider;
    
    private readonly ILog _logger = LogManager.GetLogger(typeof(EdFiOdsApiLimitOffsetPagingStreamResourcePageMessageProducer));
    
    public EdFiOdsApiLimitOffsetPagingStreamResourcePageMessageProducer(IEdFiDataSourceTotalCountProvider edFiDataSourceTotalCountProvider)
    {
        _edFiDataSourceTotalCountProvider = edFiDataSourceTotalCountProvider;
    }
    
    public async Task<IEnumerable<StreamResourcePageMessage<TItemActionMessage>>> ProduceMessagesAsync<TItemActionMessage>(
        StreamResourceMessage message, 
        Options options,
        Func<StreamResourcePageMessage<TItemActionMessage>, JObject, TItemActionMessage> createItemActionMessage,
        ITargetBlock<ErrorItemMessage> errorHandlingBlock, 
        CancellationToken cancellationToken)
    {
        if (message.ChangeWindow?.MaxChangeVersion != default(long) && message.ChangeWindow?.MaxChangeVersion != null)
        {
            _logger.Info(
                $"{message.ResourceUrl}: Retrieving total count of items in change versions {message.ChangeWindow.MinChangeVersion} to {message.ChangeWindow.MaxChangeVersion}.");
        }
        else
        {
            _logger.Info($"{message.ResourceUrl}: Retrieving total count of items.");
        }

        // Get total count of items in source resource for change window (if applicable)
        var (totalCountSuccess, totalCount) = await _edFiDataSourceTotalCountProvider.TryGetTotalCountAsync(
            message.ResourceUrl,
            options,
            message.ChangeWindow,
            errorHandlingBlock,
            cancellationToken);
        
        if (!totalCountSuccess)
        {
            // Allow processing to continue without performing additional work on this resource.
            return Enumerable.Empty<StreamResourcePageMessage<TItemActionMessage>>();
        }

        _logger.Info($"{message.ResourceUrl}: Total count = {totalCount}");

        long offset = 0;
        int limit = message.PageSize;

        var pageMessages = new List<StreamResourcePageMessage<TItemActionMessage>>();

        while (offset < totalCount)
        {
            var pageMessage = new StreamResourcePageMessage<TItemActionMessage>
            {
                // Resource-specific context
                ResourceUrl = message.ResourceUrl,
                PostAuthorizationFailureRetry = message.PostAuthorizationFailureRetry,

                // Page-strategy specific context
                Limit = limit,
                Offset = offset,
                
                // Source Ed-Fi ODS API processing context (shared)
                // EdFiApiClient = message.EdFiApiClient,

                // Global processing context
                ChangeWindow = message.ChangeWindow,
                CreateItemActionMessage = createItemActionMessage,
                CancellationSource = message.CancellationSource,
            };

            pageMessages.Add(pageMessage);

            offset += limit;
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
