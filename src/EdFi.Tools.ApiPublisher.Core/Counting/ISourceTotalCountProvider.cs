// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;

namespace EdFi.Tools.ApiPublisher.Core.Counting;

public interface ISourceTotalCountProvider
{
    Task<(bool, long)> TryGetTotalCountAsync(
        string resourceUrl,
        Options options,
        ChangeWindow changeWindow,
        ITargetBlock<ErrorItemMessage> errorHandlingBlock,
        CancellationToken cancellationToken);
}
