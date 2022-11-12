// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Core.ApiClientManagement;
using log4net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Core.Versioning;

public class EdFiOdsApiVersionMetadataProviderBase
{
    private readonly string _role;
    private readonly IEdFiApiClientProvider _edFiApiClientProvider;

    private readonly ILog _logger;
    
    protected EdFiOdsApiVersionMetadataProviderBase(string role, IEdFiApiClientProvider edFiApiClientProvider)
    {
        _role = role;
        _edFiApiClientProvider = edFiApiClientProvider;
        
        _logger = LogManager.GetLogger(GetType());
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
                _logger.Info($"{_role} version information: {versionObject.ToString(Formatting.Indented)}");
            }
            catch (Exception)
            {
                throw new Exception($"Unable to parse version information returned from {_role.ToLower()} API.");
            }

            return versionObject;
        }
    }
}
