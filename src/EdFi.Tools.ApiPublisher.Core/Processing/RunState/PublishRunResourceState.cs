// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;

namespace EdFi.Tools.ApiPublisher.Core.Processing.RunState;

/// <summary>
/// How far one pass over one cursor-paged resource got (see APIPUB-142). Only cursor-paged resources
/// appear: an offset-paged read, which includes every <c>/deletes</c> and <c>/keyChanges</c>, has no
/// partition or page token to record and is read in full by a resumed run.
/// </summary>
public class PublishRunResourceState
{
    public string ResourceUrl { get; set; }

    /// <summary>
    /// Which pass over this resource URL the progress belongs to. A resource with an authorization retry
    /// pipeline is read once by the pass that reads it first and again by the retry pass after its update
    /// prerequisites complete (see APIPUB-133). Both carry the same resource URL, so their progress is kept
    /// apart: without this, the retry pass would resume from the first pass's position and skip pages it
    /// exists to read.
    /// </summary>
    public bool IsAuthorizationRetryPass { get; set; }

    public List<PublishRunPartitionState> Partitions { get; set; } = new();
}
