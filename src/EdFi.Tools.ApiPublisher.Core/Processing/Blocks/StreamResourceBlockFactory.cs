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
using Serilog;
using Serilog.Events;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Blocks
{
    public class StreamResourceBlockFactory
    {
        private readonly ILogger _logger = Log.ForContext(typeof(StreamResourceBlockFactory));
        private readonly IStreamResourcePageMessageProducer _streamResourcePageMessageProducer;

        public StreamResourceBlockFactory(IStreamResourcePageMessageProducer streamResourcePageMessageProducer)
        {
            // _edFiDataSourceTotalCountProvider = edFiDataSourceTotalCountProvider;
            _streamResourcePageMessageProducer = streamResourcePageMessageProducer;
        }

        public TransformManyBlock<StreamResourceMessage, StreamResourcePageMessage<TProcessDataMessage>> CreateBlock<TProcessDataMessage>(
            Func<StreamResourcePageMessage<TProcessDataMessage>, string, IEnumerable<TProcessDataMessage>> createProcessDataMessages,
            ITargetBlock<ErrorItemMessage> errorHandlingBlock,
            Options options,
            CancellationToken cancellationToken)
        {
            return new TransformManyBlock<StreamResourceMessage, StreamResourcePageMessage<TProcessDataMessage>>(
                async msg =>
                {
                    if (msg.CancellationSource.IsCancellationRequested)
                    {
                        _logger.Debug($"{msg.ResourceUrl}: Cancellation requested.");

                        return Enumerable.Empty<StreamResourcePageMessage<TProcessDataMessage>>();
                    }

                    try
                    {
                        var messages = await ProducePageMessagesAsync(
                                msg,
                                errorHandlingBlock,
                                options,
                                createProcessDataMessages,
                                cancellationToken)
                            .ConfigureAwait(false);

                        if (msg.CancellationSource.IsCancellationRequested)
                        {
                            _logger.Debug($"{msg.ResourceUrl}: Cancellation requested.");

                            return Enumerable.Empty<StreamResourcePageMessage<TProcessDataMessage>>();
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

        private async Task<IEnumerable<StreamResourcePageMessage<TProcessDataMessage>>> ProducePageMessagesAsync<TProcessDataMessage>(
            StreamResourceMessage message,
            ITargetBlock<ErrorItemMessage> errorHandlingBlock,
            Options options,
            Func<StreamResourcePageMessage<TProcessDataMessage>, string, IEnumerable<TProcessDataMessage>> createProcessDataMessages,
            CancellationToken cancellationToken)
        {
            // ==============================================================
            // BEGIN POSSIBLE SEAM: Dependency management
            // ==============================================================
            if (message.Dependencies.Any())
            {
                if (_logger.IsEnabled(LogEventLevel.Debug))
                {
                    _logger.Debug($"{message.ResourceUrl}: Waiting for dependencies to complete before streaming...");
                }

                // Wait for other resources to complete processing
                await Task.WhenAll(message.Dependencies).ConfigureAwait(false);

                if (_logger.IsEnabled(LogEventLevel.Debug))
                {
                    _logger.Debug($"{message.ResourceUrl}: Dependencies completed. Waiting for an available processing slot...");
                }
            }
            else
            {
                if (_logger.IsEnabled(LogEventLevel.Debug))
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

            if (_logger.IsEnabled(LogEventLevel.Debug))
            {
                _logger.Debug(
                    $"{message.ResourceUrl}: Processing slot acquired ({message.ProcessingSemaphore.CurrentCount} remaining). Starting streaming of resources...");
            }

            try
            {
                return await _streamResourcePageMessageProducer.ProduceMessagesAsync(
                    message,
                    options,
                    errorHandlingBlock,
                    createProcessDataMessages,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Error($"{message.ResourceUrl}: An unhandled exception occurred while producing streaming page messages:{Environment.NewLine}{ex}");

                throw;
            }
        }
    }
}
