// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
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

    public async Task<JObject> GetVersionMetadata()
    {
        var versionResponse = _edFiApiClientProvider.GetApiClient().HttpClient.GetAsync("");

        if (!versionResponse.Result.IsSuccessStatusCode)
        {
            throw new Exception($"{_role} API at '{_edFiApiClientProvider.GetApiClient().HttpClient.BaseAddress}' returned status code '{versionResponse.Result.StatusCode}' for request for version information.");
        }

        string responseJson = await versionResponse.Result.Content.ReadAsStringAsync().ConfigureAwait(false);

        return GetVersionObject(responseJson);

        JObject GetVersionObject(string versionJson)
        {
            JObject versionObject;

            try
            {
                versionObject = JObject.Parse(versionJson);
                var message = $"{_role} version information: {versionObject.ToString(Formatting.Indented)}";
                _logger.Information(message);
            }
            catch (Exception)
            {
                throw new Exception($"Unable to parse version information returned from {_role.ToLower()} API.");
            }

            return versionObject;
        }
    }
}
