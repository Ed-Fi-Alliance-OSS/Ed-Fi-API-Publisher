// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using System.Threading;

namespace EdFi.Tools.ApiPublisher.Core.Processing;

/// <summary>
/// Defines a method for initiating processing of resources for a particular stage of publishing.
/// </summary>
/// <remarks>Implementations should take a dependency on the <see cref="IStreamingResourceProcessor" /> to initiate processing
/// by invoking the generic <see cref="IStreamingResourceProcessor.Start{TProcessDataMessage}" /> method with the type of the
/// message appropriate for the publishing target.</remarks>
public interface IPublishingStageInitiator
{
    IDictionary<string, StreamingPagesItem> Start(ProcessingContext processingContext, CancellationToken cancellationToken);
}
