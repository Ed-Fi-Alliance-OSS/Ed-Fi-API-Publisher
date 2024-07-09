// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Handlers;

public interface IStreamResourcePageMessageHandler
{
    Task<IEnumerable<TProcessDataMessage>> HandleStreamResourcePageAsync<TProcessDataMessage>(
        StreamResourcePageMessage<TProcessDataMessage> message,
        Options options,
        ITargetBlock<ErrorItemMessage> errorHandlingBlock);
}
