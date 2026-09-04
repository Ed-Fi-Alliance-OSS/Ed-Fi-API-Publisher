// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using Serilog;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Blocks
{
    public static class ErrorSendingExtensions
    {
        private static readonly ILogger _logger = Log.Logger.ForContext(typeof(ErrorSendingExtensions));

        /// <summary>
        /// Sends an error to the (bounded) error ingestion block for publication without ever throwing into
        /// the producing block. A send parked on a full block is released by the supplied cancellation token;
        /// if cancellation has been (or gets) requested, delivery is still attempted synchronously, because
        /// graceful cancellation is a normal flow event (e.g. delete processing canceling remaining pages)
        /// and a raw <c>SendAsync</c> would instead throw <c>TaskCanceledException</c> and drop the error.
        /// </summary>
        public static async Task SendErrorAsync(
            this ITargetBlock<ErrorItemMessage> errorHandlingBlock,
            ErrorItemMessage error,
            CancellationToken cancellationToken)
        {
            try
            {
                if (!await errorHandlingBlock.SendAsync(error, cancellationToken).ConfigureAwait(false))
                {
                    _logger.Warning(
                        "{ResourceUrl}: An error could not be published because the error ingestion block declined it (it has likely completed).",
                        error.ResourceUrl);
                }
            }
            catch (System.OperationCanceledException)
            {
                // Fall back to a synchronous post so the error is still delivered when there is capacity
                if (!errorHandlingBlock.Post(error))
                {
                    _logger.Warning(
                        "{ResourceUrl}: An error could not be published because cancellation was requested while the error ingestion block was full.",
                        error.ResourceUrl);
                }
            }
        }
    }
}
