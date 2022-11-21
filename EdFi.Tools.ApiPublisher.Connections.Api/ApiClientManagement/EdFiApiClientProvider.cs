// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using log4net;

namespace EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;

public class EdFiApiClientProvider : ISourceEdFiApiClientProvider, ITargetEdFiApiClientProvider
{
    private readonly Lazy<EdFiApiClient> _apiClient;

    private readonly ILog _logger = LogManager.GetLogger(typeof(EdFiApiClientProvider));
    
    public EdFiApiClientProvider(Lazy<EdFiApiClient> apiClient)
    {
        _apiClient = apiClient;
    }
    
    public EdFiApiClient GetApiClient()
    {
        if (!_apiClient.IsValueCreated)
        {
            // Establish connection to API
            _logger.Info($"Initializing API client '{_apiClient.Value.ConnectionDetails.Name}'...");
        }

        return _apiClient.Value;
    }
}
