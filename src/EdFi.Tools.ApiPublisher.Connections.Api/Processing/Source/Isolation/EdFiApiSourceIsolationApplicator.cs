// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Helpers;
using EdFi.Tools.ApiPublisher.Core.Isolation;
using Newtonsoft.Json.Linq;
using Serilog;
using System.Net;
using Version = EdFi.Tools.ApiPublisher.Core.Helpers.Version;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Isolation;

public class EdFiApiSourceIsolationApplicator : ISourceIsolationApplicator
{
    private readonly ISourceEdFiApiClientProvider _sourceEdFiApiClientProvider;

    private readonly ILogger _logger = Log.ForContext(typeof(EdFiApiSourceIsolationApplicator));

    public EdFiApiSourceIsolationApplicator(ISourceEdFiApiClientProvider sourceEdFiApiClientProvider)
    {
        _sourceEdFiApiClientProvider = sourceEdFiApiClientProvider;
    }

    public async Task ApplySourceSnapshotIdentifierAsync(Version sourceApiVersion)
    {
        var sourceApiClient = _sourceEdFiApiClientProvider.GetApiClient();
        var sourceApiConnectionDetails = sourceApiClient.ConnectionDetails;
        if (sourceApiVersion.Major >= 7)
        {
            sourceApiClient.HttpClient.DefaultRequestHeaders.Add("Use-Snapshot", "true");
        }
        else
        {
            string snapshotIdentifier =
            await GetSourceSnapshotIdentifierAsync(sourceApiClient, sourceApiVersion).ConfigureAwait(false);

            // Confirm that a snapshot exists or --ignoreIsolation=true has been provided
            if (snapshotIdentifier == null)
            {
                string message =
                    $"Snapshot identifier could not be obtained from API at '{sourceApiConnectionDetails.Url}', and \"force\" option was not specified. Publishing cannot proceed due to lack of guaranteed isolation from ongoing changes at the source. Use --ignoreIsolation=true (or a corresponding configuration value) to force processing.";

                throw new Exception(message);
            }

            // Configure source HTTP client to add the snapshot identifier header to every request against the source API
            sourceApiClient.HttpClient.DefaultRequestHeaders.Add("Snapshot-Identifier", snapshotIdentifier);
        }
    }

    private async Task<string> GetSourceSnapshotIdentifierAsync(EdFiApiClient sourceApiClient, Version sourceApiVersion)
    {
        string snapshotsRelativePath;

        // Get available snapshot information
        if (sourceApiVersion.IsAtLeast(5, 2))
        {
            snapshotsRelativePath = $"{sourceApiClient.ChangeQueriesApiSegment}/snapshots";
        }
        else
        {
            snapshotsRelativePath = $"{sourceApiClient.DataManagementApiSegment}/publishing/snapshots";
        }

        var snapshotsResponse = await sourceApiClient.HttpClient.GetAsync(snapshotsRelativePath).ConfigureAwait(false);

        if (snapshotsResponse.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.Warning(
                $"Source API at '{sourceApiClient.HttpClient.BaseAddress}' does not support the necessary isolation for reliable API publishing. Errors may occur, or some data may not be published without causing failures.");

            return null;
        }

        if (snapshotsResponse.StatusCode == HttpStatusCode.Forbidden)
        {
            _logger.Warning(
                $"The API publisher does not have permissions to access the source API's 'snapshots' resource at '{sourceApiClient.HttpClient.BaseAddress}{snapshotsRelativePath}'. Make sure that the source API is using a correctly configured claim set for your API Publisher's API client.");

            return null;
        }

        if (snapshotsResponse.IsSuccessStatusCode)
        {
            // Detect null content and provide a better error message (which happens during unit testing if mocked requests aren't properly defined)
            if (snapshotsResponse.Content == null)
            {
                throw new NullReferenceException(
                    $"Content of response for '{sourceApiClient.HttpClient.BaseAddress}{snapshotsRelativePath}' was null.");
            }

            string snapshotResponseText = await snapshotsResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

            var snapshotResponseArray = JArray.Parse(snapshotResponseText);

            if (!snapshotResponseArray.Any())
            {
                // No snapshots available.
                _logger.Warning(
                    $"Snapshots are supported, but no snapshots are available from source API at '{sourceApiClient.HttpClient.BaseAddress}{snapshotsRelativePath}'.");

                return null;
            }

            var snapshot = snapshotResponseArray.Select(
                    jt =>
                    {
                        string snapshotIdentifier = jt["snapshotIdentifier"].Value<string>();
                        string snapshotDateTimeText = jt["snapshotDateTime"].Value<string>();

                        if (!DateTime.TryParse(snapshotDateTimeText, out var snapshotDateTimeValue))
                        {
                            snapshotDateTimeValue = DateTime.MinValue;
                        }

                        return new
                        {
                            SnapshotIdentifier = snapshotIdentifier,
                            SnapshotDateTime = snapshotDateTimeValue,
                            SnapshotDateTimeText = snapshotDateTimeText
                        };
                    })
                .OrderByDescending(x => x.SnapshotDateTime)
                .First();

            _logger.Information($"Using snapshot identifier '{snapshot.SnapshotIdentifier}' created at '{snapshot.SnapshotDateTime}'.");

            return snapshot.SnapshotIdentifier;
        }

        string errorResponseText = await snapshotsResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

        _logger.Error(
            $"Unable to get snapshot identifier from API at '{sourceApiClient.HttpClient.BaseAddress}{snapshotsRelativePath}'. Request for available snapshots returned status '{snapshotsResponse.StatusCode}' with message body: {errorResponseText}");

        return null;
    }
}
