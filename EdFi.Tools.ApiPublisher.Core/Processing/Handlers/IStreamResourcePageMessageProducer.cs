// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Core.Processing.Handlers;

public interface IStreamResourcePageMessageProducer
{
    Task<IEnumerable<StreamResourcePageMessage<TItemActionMessage>>> ProduceMessagesAsync<TItemActionMessage>(
        StreamResourceMessage message, 
        Options options,
        Func<StreamResourcePageMessage<TItemActionMessage>, JObject, TItemActionMessage> createItemActionMessage,
        ITargetBlock<ErrorItemMessage> errorHandlingBlock, 
        CancellationToken cancellationToken);
}