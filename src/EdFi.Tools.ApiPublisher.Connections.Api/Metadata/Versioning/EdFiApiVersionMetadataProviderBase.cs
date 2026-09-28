// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Metadata.Versioning;

public class EdFiApiVersionMetadataProviderBase
{
    private readonly string _role;
    private readonly IEdFiApiClientProvider _edFiApiClientProvider;

    private readonly ILogger _logger;

    public EdFiApiVersionMetadataProviderBase(string role, IEdFiApiClientProvider edFiApiClientProvider)
    {
        _role = role;
        _edFiApiClientProvider = edFiApiClientProvider;

        _logger = Log.ForContext(GetType());
    }

    /// <summary>
    /// Returns the API's Discovery document, which carries its version information as well as the paths it
    /// serves.
    /// </summary>
    /// <remarks>
    /// Taken from the copy the client read while the connection was being set up rather than requested again.
    /// The same document answers the version check, the dependency metadata URL and the path segments, so
    /// asking for it once is both fewer requests and one answer that cannot disagree with itself.
    /// </remarks>
    public Task<JObject> GetVersionMetadata()
    {
        var edFiApiClient = _edFiApiClientProvider.GetApiClient();
        var discoveryDocument = edFiApiClient.DiscoveryDocument;

        if (!discoveryDocument.WasRead)
        {
            // Told apart because the exit code is what a scheduled job acts on. An API that answered with
            // something a document cannot be read from is not going to answer differently on a retry, so
            // reporting that as an incomplete run invites a job to keep rerunning a configuration fault.
            // Being unable to reach the API at all is the case a later run may well resolve.
            if (discoveryDocument.Outcome == DiscoveryOutcome.NotADocument)
            {
                throw new InvalidConfigurationException(
                    $"{_role} API at '{edFiApiClient.HttpClient.BaseAddress}' did not provide version information: it answered, but not with a Discovery document. The reason was reported while the connection was being set up. Check that this URL addresses an Ed-Fi API, and that whatever sits in front of it serves the document at its root.");
            }

            throw new Exception(
                $"{_role} API at '{edFiApiClient.HttpClient.BaseAddress}' did not provide version information, because it could not be reached. The reason was reported while the connection was being set up.");
        }

        // The document is remote content, so it is a property rather than part of the template. Interpolating
        // it into the template would let a value such as a route placeholder be parsed as a property token.
        _logger.Information(
            "{Role:l} version information: {VersionInformation}",
            _role,
            discoveryDocument.Content.ToString(Formatting.Indented));

        return Task.FromResult(discoveryDocument.Content);
    }
}
