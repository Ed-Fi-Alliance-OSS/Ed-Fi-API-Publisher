// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Core.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Processing;
using log4net;

namespace EdFi.Tools.ApiPublisher.Core.Capabilities;

public class EdFiApiDataSourceCapabilities : IDataSourceCapabilities
{
    private readonly ISourceEdFiApiClientProvider _sourceEdFiApiClientProvider;

    private readonly ILog _logger = LogManager.GetLogger(typeof(EdFiApiDataSourceCapabilities));
    
    public EdFiApiDataSourceCapabilities(ISourceEdFiApiClientProvider sourceEdFiApiClientProvider)
    {
        _sourceEdFiApiClientProvider = sourceEdFiApiClientProvider;
    }
    
    public async Task<bool> SupportsKeyChangesAsync(string probeResourceKey)
    {
        var edFiApiClient = _sourceEdFiApiClientProvider.GetApiClient();
        
        string probeUrl = $"{edFiApiClient.DataManagementApiSegment}{probeResourceKey}{EdFiApiConstants.KeyChangesPathSuffix}";

        _logger.Debug($"Probing source API for key changes support at '{probeUrl}'.");

        var probeResponse = await edFiApiClient.HttpClient.GetAsync($"{probeUrl}?limit=1").ConfigureAwait(false);

        if (probeResponse.IsSuccessStatusCode)
        {
            _logger.Debug($"Probe response status was '{probeResponse.StatusCode}'.");
            return true;
        }

        _logger.Warn($"Request to Source API for the '{EdFiApiConstants.KeyChangesPathSuffix}' child resource was unsuccessful (response status was '{probeResponse.StatusCode}'). Key change processing cannot be performed.");

        return false;
    }

    public async Task<bool> SupportsDeletesAsync(string probeResourceKey)
    {
        var edFiApiClient = _sourceEdFiApiClientProvider.GetApiClient();

        // Probe for deletes support
        string probeUrl = $"{edFiApiClient.DataManagementApiSegment}{probeResourceKey}{EdFiApiConstants.DeletesPathSuffix}";

        _logger.Debug($"Probing source API for deletes support at '{probeUrl}'.");

        var probeResponse = await edFiApiClient.HttpClient.GetAsync($"{probeUrl}?limit=1").ConfigureAwait(false);

        if (probeResponse.IsSuccessStatusCode)
        {
            _logger.Debug($"Probe response status was '{probeResponse.StatusCode}'.");
            return true;
        }

        _logger.Warn($"Request to Source API for the '{EdFiApiConstants.DeletesPathSuffix}' child resource was unsuccessful (response status was '{probeResponse.StatusCode}'). Delete processing cannot be performed.");

        return false;
    }
}
