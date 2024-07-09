// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Processing.Blocks;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using System.Collections.Generic;
using System.Threading.Tasks.Dataflow;

namespace EdFi.Tools.ApiPublisher.Core.Processing;

/// <summary>
/// Defines methods for integrating custom data processing into the pipeline.
/// </summary>
public interface IProcessingBlocksFactory<TProcessDataMessage>
{
    /// <summary>
    /// Creates the blocks for use in the processing pipeline that receives the "process data" messages, and produces 0 or more
    /// <see cref="ErrorItemMessage" /> instances as output to the final stage of the whole pipeline (error publishing). 
    /// </summary>
    /// <param name="createBlocksRequest"></param>
    /// <returns>A tuple containing the input and output blocks representing the custom data processing.</returns>
    (ITargetBlock<TProcessDataMessage>, ISourceBlock<ErrorItemMessage>) CreateProcessingBlocks(CreateBlocksRequest createBlocksRequest);

    /// <summary>
    /// Creates the custom data processing message(s) from the supplied page-level JSON content.
    /// </summary>
    /// <param name="message">The message containing the context of the current streaming resource.</param>
    /// <param name="json">The JSON payload for the current page of data.</param>
    /// <returns>An enumerable of 0 or more messages representing the data to be processed.</returns>
    IEnumerable<TProcessDataMessage> CreateProcessDataMessages(StreamResourcePageMessage<TProcessDataMessage> message, string json);
}
