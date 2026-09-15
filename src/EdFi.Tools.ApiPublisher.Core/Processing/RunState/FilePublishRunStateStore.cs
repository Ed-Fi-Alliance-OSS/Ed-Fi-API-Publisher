// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using Newtonsoft.Json;
using Serilog;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Core.Processing.RunState;

/// <summary>
/// Keeps the run state in a local JSON file (see APIPUB-142). A resume is a single-machine operation, and
/// a file is the one backing store that works for every connection shape, named or not, Plaintext
/// included. The path is configurable so a containerised run can put it on a mounted volume.
/// </summary>
/// <remarks>
/// The configuration stores are not used for this. Their contract is one JSON value per key, read and
/// rewritten whole with no compare-and-swap, and AWS SSM caps a parameter value at 4 KB (8 KB advanced),
/// which per-partition page tokens for a full publish exceed by more than an order of magnitude.
/// </remarks>
public class FilePublishRunStateStore : IPublishRunStateStore
{
    /// <summary>
    /// File name used when no path is configured, and when the configured path is a directory. The publication
    /// the state belongs to is worked into it (see <see cref="BuildDefaultFileName" />), so that two runs
    /// sharing a directory do not overwrite each other's progress.
    /// </summary>
    public const string DefaultFileNamePrefix = "api-publisher-run-state";

    private const string DefaultFileExtension = ".json";

    private readonly ILogger _logger = Log.ForContext(typeof(FilePublishRunStateStore));

    private bool _writeFailureReported;

    public FilePublishRunStateStore(
        Options options,
        ISourceConnectionDetails sourceConnectionDetails,
        ITargetConnectionDetails targetConnectionDetails)
    {
        ArgumentNullException.ThrowIfNull(options);

        Location = ResolvePath(
            options.RunStatePath,
            BuildDefaultFileName(
                sourceConnectionDetails?.Name,
                targetConnectionDetails?.Name,
                options.LastChangeVersionProcessedNamespace));
    }

    /// <summary>
    /// Names the default file after the publication it records, so that publications sharing a working
    /// directory keep their own progress. Running several at once is an ordinary shape for this tool, and one
    /// file for all of them means the last writer wins and the others silently lose their resume. The change
    /// version namespace is part of that name because two publications can otherwise share a source and a
    /// target and be told apart only by it. Falls back to the bare name when the connections are unnamed,
    /// which is a run that cannot be resumed anyway.
    /// </summary>
    public static string BuildDefaultFileName(
        string sourceConnectionName,
        string targetConnectionName,
        string lastChangeVersionProcessedNamespace = null)
    {
        if (string.IsNullOrWhiteSpace(sourceConnectionName) || string.IsNullOrWhiteSpace(targetConnectionName))
        {
            return DefaultFileNamePrefix + DefaultFileExtension;
        }

        string publication = $"{MakeFileNameSafe(sourceConnectionName)}-to-{MakeFileNameSafe(targetConnectionName)}";

        if (!string.IsNullOrWhiteSpace(lastChangeVersionProcessedNamespace))
        {
            publication = $"{MakeFileNameSafe(lastChangeVersionProcessedNamespace)}-{publication}";
        }

        return $"{DefaultFileNamePrefix}-{publication}{DefaultFileExtension}";
    }

    private static string MakeFileNameSafe(string value)
    {
        var safe = value.Trim().ToCharArray();

        for (int i = 0; i < safe.Length; i++)
        {
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), safe[i]) >= 0)
            {
                safe[i] = '_';
            }
        }

        return new string(safe);
    }

    public string Location { get; }

    public async Task<PublishRunState> TryLoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(Location))
        {
            return null;
        }

        try
        {
            string json = await File.ReadAllTextAsync(Location, cancellationToken).ConfigureAwait(false);

            var state = JsonConvert.DeserializeObject<PublishRunState>(json);

            if (state is null)
            {
                _logger.Warning("Run state file '{Location}' is empty. The run will start from the beginning.", Location);

                return null;
            }

            if (!IsUsable(state, out string problem))
            {
                _logger.Warning(
                    "Run state file '{Location}' cannot be used because {Problem:l}. The run will start from the beginning.",
                    Location, problem);

                return null;
            }

            return state;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A state file that cannot be read back (truncated by a killed process, edited by hand) costs the
            // run its resume, never the run itself
            _logger.Warning(ex, "Run state file '{Location}' could not be read. The run will start from the beginning.", Location);

            return null;
        }
    }

    public async Task SaveAsync(PublishRunState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        state.UpdatedAt = DateTime.UtcNow;

        try
        {
            string directory = Path.GetDirectoryName(Location);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Written beside the target and moved over it, so a process killed mid-write leaves either the
            // previous state or the new one, and never half of either
            string temporaryPath = $"{Location}.tmp";

            await File.WriteAllTextAsync(
                    temporaryPath,
                    JsonConvert.SerializeObject(state, Formatting.Indented),
                    cancellationToken)
                .ConfigureAwait(false);

            File.Move(temporaryPath, Location, overwrite: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Reported once: a run whose state cannot be written still publishes correctly, it just cannot be
            // resumed, and a periodic write would otherwise repeat this for the length of the run
            if (!_writeFailureReported)
            {
                _writeFailureReported = true;

                _logger.Warning(ex, "Run state could not be written to '{Location}'. This run will publish normally but will not be resumable.", Location);
            }
        }
    }

    public Task DeleteAsync(CancellationToken cancellationToken)
    {
        try
        {
            File.Delete(Location);
        }
        catch (Exception ex)
        {
            // Left behind, the state is refused by the next run's match check or replaced by its first write,
            // so a failed delete is not worth failing a run that has just succeeded
            _logger.Warning(ex, "Run state file '{Location}' could not be removed.", Location);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Whether the state is something a run can act on. Reading it back without error is not the same thing:
    /// a file edited by hand, or written by something other than this publisher, can be valid JSON and still
    /// leave out a list the resume walks or half of the window it replays. Checked here so that such a file
    /// costs the run its resume and not the run, which is what this store promises everywhere else.
    /// </summary>
    private static bool IsUsable(PublishRunState state, out string problem)
    {
        if (state.Resources is null)
        {
            problem = "it records no list of resources at all";

            return false;
        }

        foreach (var resource in state.Resources)
        {
            if (resource?.Partitions is null || resource.Partitions.Exists(partition => partition is null))
            {
                problem = "one of its resources is missing the partitions it recorded";

                return false;
            }
        }

        // Both ends are written together or not at all, so one of them alone is an edited file. Taken as it
        // stands it would read as no window, turning a resumed incremental publish into a full one in silence.
        if (state.MinChangeVersion.HasValue != state.MaxChangeVersion.HasValue)
        {
            problem = "it records only one end of a change window, so the window it pinned cannot be replayed";

            return false;
        }

        if (state.MinChangeVersion > state.MaxChangeVersion)
        {
            problem = "the change window it pinned ends before it begins";

            return false;
        }

        problem = null;

        return true;
    }

    /// <summary>
    /// Resolves the configured path to the state file: a directory (existing, or written with a trailing
    /// separator) takes the default file name inside it, so that pointing the option at a mounted volume
    /// works without naming the file.
    /// </summary>
    private static string ResolvePath(string configuredPath, string defaultFileName)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.Combine(Environment.CurrentDirectory, defaultFileName);
        }

        string path = Path.GetFullPath(configuredPath.Trim());

        bool namesADirectory = Directory.Exists(path)
            || configuredPath.EndsWith(Path.DirectorySeparatorChar)
            || configuredPath.EndsWith(Path.AltDirectorySeparatorChar);

        return namesADirectory ? Path.Combine(path, defaultFileName) : path;
    }
}
