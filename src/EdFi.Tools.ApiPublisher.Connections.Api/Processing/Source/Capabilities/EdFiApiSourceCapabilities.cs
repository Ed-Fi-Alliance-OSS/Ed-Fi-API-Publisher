// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Capabilities;
using EdFi.Tools.ApiPublisher.Core.Helpers;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Versioning;
using Newtonsoft.Json.Linq;
using Serilog;
using Version = EdFi.Tools.ApiPublisher.Core.Helpers.Version;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Processing.Source.Capabilities;

public class EdFiApiSourceCapabilities : ISourceCapabilities
{
    private readonly ISourceEdFiApiClientProvider _sourceEdFiApiClientProvider;
    private readonly ISourceEdFiApiVersionMetadataProvider _sourceEdFiApiVersionMetadataProvider;

    private readonly ILogger _logger = Log.ForContext(typeof(EdFiApiSourceCapabilities));

    // Cursor paging support is a property of the source, not of a resource: determined once per run (see APIPUB-139)
    private readonly object _cursorPagingLock = new();
    private Task<bool> _supportsCursorPaging;

    public EdFiApiSourceCapabilities(
        ISourceEdFiApiClientProvider sourceEdFiApiClientProvider,
        ISourceEdFiApiVersionMetadataProvider sourceEdFiApiVersionMetadataProvider)
    {
        _sourceEdFiApiClientProvider = sourceEdFiApiClientProvider
            ?? throw new ArgumentNullException(nameof(sourceEdFiApiClientProvider));

        _sourceEdFiApiVersionMetadataProvider = sourceEdFiApiVersionMetadataProvider
            ?? throw new ArgumentNullException(nameof(sourceEdFiApiVersionMetadataProvider));
    }

    public async Task<bool> SupportsKeyChangesAsync(string probeResourceKey)
    {
        var edFiApiClient = _sourceEdFiApiClientProvider.GetApiClient();

        string probeUrl = $"{edFiApiClient.DataManagementApiSegment}{probeResourceKey}{EdFiApiConstants.KeyChangesPathSuffix}";

        _logger.Debug("Probing source API for key changes support at '{ProbeUrl}'.", probeUrl);

        var probeResponse = await edFiApiClient.HttpClient.GetAsync($"{probeUrl}?limit=1").ConfigureAwait(false);

        if (probeResponse.IsSuccessStatusCode)
        {
            _logger.Debug("Probe response status was '{StatusCode}'.", probeResponse.StatusCode);
            return true;
        }

        _logger.Warning("Request to Source API for the '{KeyChangesPathSuffix}' child resource was unsuccessful (response status was '{StatusCode}'). Key change processing cannot be performed.",
            EdFiApiConstants.KeyChangesPathSuffix, probeResponse.StatusCode);

        return false;
    }

    public async Task<bool> SupportsDeletesAsync(string probeResourceKey)
    {
        var edFiApiClient = _sourceEdFiApiClientProvider.GetApiClient();

        // Probe for deletes support
        string probeUrl = $"{edFiApiClient.DataManagementApiSegment}{probeResourceKey}{EdFiApiConstants.DeletesPathSuffix}";

        _logger.Debug("Probing source API for deletes support at '{ProbeUrl}'.", probeUrl);

        var probeResponse = await edFiApiClient.HttpClient.GetAsync($"{probeUrl}?limit=1").ConfigureAwait(false);

        if (probeResponse.IsSuccessStatusCode)
        {
            _logger.Debug("Probe response status was '{StatusCode}'.", probeResponse.StatusCode);
            return true;
        }

        _logger.Warning("Request to Source API for the '{DeletesPathSuffix}' child resource was unsuccessful (response status was '{StatusCode}'). Delete processing cannot be performed.",
            EdFiApiConstants.DeletesPathSuffix, probeResponse.StatusCode);

        return false;
    }

    public Task<bool> SupportsCursorPagingAsync(string probeResourceKey)
    {
        lock (_cursorPagingLock)
        {
            return _supportsCursorPaging ??= ProbeCursorPagingSupportAsync(probeResourceKey);
        }
    }

    private async Task<bool> ProbeCursorPagingSupportAsync(string probeResourceKey)
    {
        // Version gate first: pre-7.3 sources ignore unknown query string parameters, so only a version check plus
        // a /partitions probe is trustworthy -- never a trial pageToken request (see APIPUB-136)
        Version sourceApiVersion;

        try
        {
            var versionMetadata = await _sourceEdFiApiVersionMetadataProvider.GetVersionMetadata().ConfigureAwait(false);
            sourceApiVersion = new Version(versionMetadata.Value<string>("version"));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Source API version could not be determined. Cursor paging will not be used (offset/limit paging applies).");
            return false;
        }

        if (!sourceApiVersion.IsAtLeast(7, 3))
        {
            _logger.Information("Source API version {SourceApiVersion} predates cursor paging support (7.3). Offset/limit paging will be used.", sourceApiVersion);
            return false;
        }

        var edFiApiClient = _sourceEdFiApiClientProvider.GetApiClient();
        string probeUrl = $"{edFiApiClient.DataManagementApiSegment}{probeResourceKey}{EdFiApiConstants.PartitionsPathSuffix}";

        _logger.Debug("Probing source API for cursor paging support at '{ProbeUrl}'.", probeUrl);

        try
        {
            using var probeResponse = await edFiApiClient.HttpClient.GetAsync($"{probeUrl}?number=1").ConfigureAwait(false);

            if (!probeResponse.IsSuccessStatusCode)
            {
                _logger.Warning("Request to Source API for the '{PartitionsPathSuffix}' child resource was unsuccessful (response status was '{StatusCode}'). Offset/limit paging will be used instead of cursor paging.",
                    EdFiApiConstants.PartitionsPathSuffix, probeResponse.StatusCode);

                return false;
            }

            string content = await probeResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (JObject.Parse(content)["pageTokens"] is not JArray)
            {
                _logger.Warning("Response from Source API for the '{PartitionsPathSuffix}' child resource did not contain a 'pageTokens' array. Offset/limit paging will be used instead of cursor paging.",
                    EdFiApiConstants.PartitionsPathSuffix);

                return false;
            }

            _logger.Debug("Probe response status was '{StatusCode}'. Source supports cursor paging.", probeResponse.StatusCode);

            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Probe of Source API for cursor paging support at '{ProbeUrl}' failed. Offset/limit paging will be used instead of cursor paging.", probeUrl);

            return false;
        }
    }

    public bool SupportsGetItemById
    {
        get => true;
    }
}
