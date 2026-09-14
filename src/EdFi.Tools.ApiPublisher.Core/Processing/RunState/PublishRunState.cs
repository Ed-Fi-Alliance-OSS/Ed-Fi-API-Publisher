// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.Reflection;

namespace EdFi.Tools.ApiPublisher.Core.Processing.RunState;

/// <summary>
/// What a run has got through, persisted so that a run which failed partway can be resumed instead of
/// being read from the beginning (see APIPUB-142). This is operational state, not configuration: it has
/// its own lifetime (removed once a run completes without losing a document) and is deliberately kept out
/// of the configuration stores, whose contract is a single read-modify-written JSON value per key.
/// </summary>
/// <remarks>
/// The change window is pinned rather than the source snapshot. On 7.x, which is the only line that can
/// use cursor paging, the publisher reads through the boolean <c>Use-Snapshot</c> header and is given no
/// snapshot identity it could store or ask for again. The window does that work instead: anything written
/// to the source after the original run started carries a change version above
/// <see cref="MaxChangeVersion" />, so it is out of scope for the resumed run and still in scope for the
/// next one.
/// </remarks>
public class PublishRunState
{
    /// <summary>Identifies the run the state belongs to. Reported in log lines, never matched on.</summary>
    public string RunId { get; set; }

    /// <summary>
    /// The publisher that wrote the state. A resume is refused across versions, because what is recorded
    /// here means what the publisher that wrote it meant by it.
    /// </summary>
    public string PublisherVersion { get; set; }

    public string SourceConnectionName { get; set; }

    public string TargetConnectionName { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// The pinned change window, null on both when the run has no change window (a full publish). Replayed
    /// verbatim on resume rather than recomputed, so the resumed run reads the window that was in force
    /// when the original run started.
    /// </summary>
    public long? MinChangeVersion { get; set; }

    public long? MaxChangeVersion { get; set; }

    /// <summary>
    /// How far each cursor-paged resource got, one entry per resource URL and pass.
    /// </summary>
    public List<PublishRunResourceState> Resources { get; set; } = new();

    /// <summary>
    /// Finds the recorded progress for one pass over one resource, or null when the run has none for it.
    /// </summary>
    public PublishRunResourceState FindResource(string resourceUrl, bool isAuthorizationRetryPass)
        => Resources?.Find(
            resource => string.Equals(resource.ResourceUrl, resourceUrl, StringComparison.OrdinalIgnoreCase)
                && resource.IsAuthorizationRetryPass == isAuthorizationRetryPass);

    /// <summary>
    /// Starts the state for a new run. The change window may be null; see <see cref="MinChangeVersion" />.
    /// </summary>
    public static PublishRunState StartNew(
        string sourceConnectionName,
        string targetConnectionName,
        ChangeWindow changeWindow)
    {
        var now = DateTime.UtcNow;

        return new PublishRunState
        {
            RunId = Guid.NewGuid().ToString("N"),
            PublisherVersion = CurrentPublisherVersion,
            SourceConnectionName = sourceConnectionName,
            TargetConnectionName = targetConnectionName,
            StartedAt = now,
            UpdatedAt = now,
            MinChangeVersion = changeWindow?.MinChangeVersion,
            MaxChangeVersion = changeWindow?.MaxChangeVersion,
        };
    }

    /// <summary>
    /// Gets the pinned change window, or null when the run had none.
    /// </summary>
    public ChangeWindow GetPinnedChangeWindow()
        => MinChangeVersion.HasValue && MaxChangeVersion.HasValue
            ? new ChangeWindow
            {
                MinChangeVersion = MinChangeVersion.Value,
                MaxChangeVersion = MaxChangeVersion.Value,
            }
            : null;

    /// <summary>
    /// Whether the state describes the same work this run is about to do. A resume against a different
    /// source, a different target or a different publisher build starts over instead, because nothing
    /// recorded here would mean the same thing.
    /// </summary>
    public bool Matches(string sourceConnectionName, string targetConnectionName, out string mismatchReason)
    {
        if (!string.Equals(SourceConnectionName, sourceConnectionName, StringComparison.Ordinal))
        {
            mismatchReason =
                $"it was written for source connection '{Describe(SourceConnectionName)}' and this run publishes from '{Describe(sourceConnectionName)}'";

            return false;
        }

        if (!string.Equals(TargetConnectionName, targetConnectionName, StringComparison.Ordinal))
        {
            mismatchReason =
                $"it was written for target connection '{Describe(TargetConnectionName)}' and this run publishes to '{Describe(targetConnectionName)}'";

            return false;
        }

        if (!string.Equals(PublisherVersion, CurrentPublisherVersion, StringComparison.Ordinal))
        {
            mismatchReason =
                $"it was written by API Publisher {Describe(PublisherVersion)} and this is {Describe(CurrentPublisherVersion)}";

            return false;
        }

        mismatchReason = null;

        return true;

        static string Describe(string value) => string.IsNullOrEmpty(value) ? "(unnamed)" : value;
    }

    private static string CurrentPublisherVersion { get; } =
        typeof(PublishRunState).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(PublishRunState).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
