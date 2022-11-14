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
using EdFi.Tools.ApiPublisher.Core.Processing.Handlers;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using log4net;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Blocks
{
    public class StreamResourceBlockFactory
    {
        private readonly ILog _logger = LogManager.GetLogger(typeof(StreamResourceBlockFactory));
        private readonly IStreamResourcePageMessageProducer _streamResourcePageMessageProducer;

        public StreamResourceBlockFactory(IStreamResourcePageMessageProducer streamResourcePageMessageProducer)
        {
            // _edFiDataSourceTotalCountProvider = edFiDataSourceTotalCountProvider;
            _streamResourcePageMessageProducer = streamResourcePageMessageProducer;
        }

        public TransformManyBlock<StreamResourceMessage, StreamResourcePageMessage<TItemActionMessage>>
            CreateBlock<TItemActionMessage>(
            Func<StreamResourcePageMessage<TItemActionMessage>, JObject, TItemActionMessage> createItemActionMessage,
            ITargetBlock<ErrorItemMessage> errorHandlingBlock,
            Options options,
            CancellationToken cancellationToken)
        {
            return new TransformManyBlock<StreamResourceMessage, StreamResourcePageMessage<TItemActionMessage>>(
                async msg =>
                {
                    if (msg.CancellationSource.IsCancellationRequested)
                    {
                        _logger.Debug($"{msg.ResourceUrl}: Cancellation requested.");

                        return Enumerable.Empty<StreamResourcePageMessage<TItemActionMessage>>();
                    }

                    try
                    {
                        var messages = await DoStreamResource(
                                msg,
                                createItemActionMessage,
                                errorHandlingBlock,
                                options,
                                cancellationToken)
                            .ConfigureAwait(false);

                        if (msg.CancellationSource.IsCancellationRequested)
                        {
                            _logger.Debug($"{msg.ResourceUrl}: Cancellation requested.");

                            return Enumerable.Empty<StreamResourcePageMessage<TItemActionMessage>>();
                        }

                        return messages;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"{msg.ResourceUrl}: An unhandled exception occurred in the StreamResource block: {ex}");

                        throw;
                    }
                });
        }

        private async Task<IEnumerable<StreamResourcePageMessage<TItemActionMessage>>> DoStreamResource<TItemActionMessage>(
            StreamResourceMessage message,
            Func<StreamResourcePageMessage<TItemActionMessage>, JObject, TItemActionMessage> createItemActionMessage,
            ITargetBlock<ErrorItemMessage> errorHandlingBlock,
            Options options,
            CancellationToken cancellationToken)
        {
            // ==============================================================
            // BEGIN POSSIBLE SEAM: Dependency management
            // ==============================================================
            if (message.Dependencies.Any())
            {
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"{message.ResourceUrl}: Waiting for dependencies to complete before streaming...");
                }

                // Wait for other resources to complete processing
                await Task.WhenAll(message.Dependencies).ConfigureAwait(false);

                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"{message.ResourceUrl}: Dependencies completed. Waiting for an available processing slot...");
                }
            }
            else
            {
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug(
                        $"{message.ResourceUrl}: Resource has no dependencies. Waiting for an available processing slot...");
                }
            }

            // ==============================================================
            // END POSSIBLE SEAM: Dependency management
            // ==============================================================

            // Wait for an available processing slot
            await message.ProcessingSemaphore.WaitAsync(cancellationToken);

            if (_logger.IsDebugEnabled)
            {
                _logger.Debug(
                    $"{message.ResourceUrl}: Processing slot acquired ({message.ProcessingSemaphore.CurrentCount} remaining). Starting streaming of resources...");
            }

            try
            {
                return await _streamResourcePageMessageProducer.ProduceMessagesAsync(
                    message,
                    options,
                    createItemActionMessage,
                    errorHandlingBlock,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Error($"{message.ResourceUrl}: {ex}");

                return Enumerable.Empty<StreamResourcePageMessage<TItemActionMessage>>();
            }
        }
    }
}
