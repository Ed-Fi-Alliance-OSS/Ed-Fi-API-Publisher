// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Threading;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Core.Processing.RunState;

/// <summary>
/// Reads and writes the run state a resume needs (see APIPUB-142). Separate from
/// <see cref="IChangeVersionProcessedWriter" /> on purpose: that records the one change version a
/// completed run earned, in the configuration store, and lives as long as the connection pairing does.
/// This records where a run got to, and is removed as soon as the run finishes without losing anything.
/// </summary>
public interface IPublishRunStateStore
{
    /// <summary>
    /// Reads the stored state, or returns null when there is none. A stored state that cannot be read
    /// back (truncated by a killed process, edited by hand) is reported as null with a Warning rather
    /// than throwing: failing to resume is never a reason to fail a run that could start over.
    /// </summary>
    Task<PublishRunState> TryLoadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes the state, replacing what is stored. Implementations write atomically, so a process killed
    /// mid-write leaves either the previous state or the new one, never half of either.
    /// </summary>
    Task SaveAsync(PublishRunState state, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the stored state. Does nothing when there is none.
    /// </summary>
    Task DeleteAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Where the state is kept, for the log lines that tell an operator what to point a resume at.
    /// </summary>
    string Location { get; }
}
