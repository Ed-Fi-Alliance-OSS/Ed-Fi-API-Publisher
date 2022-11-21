// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Core.Configuration;

public static class ConfigurationHelper
{
    public static string GetConfigurationStoreProviderName(IConfigurationRoot configurationRoot)
    {
        var configurationStoreSection = configurationRoot.GetSection("configurationStore");
        
        string configurationProviderName = configurationStoreSection.GetValue<string>("provider");

        return configurationProviderName;
    }

    public static string GetSourceConnectionType(IConfigurationRoot configurationRoot)
    {
        var connectionsConfiguration = configurationRoot.GetSection("Connections");
        var sourceConnectionConfiguration = connectionsConfiguration.GetSection("Source");

        return sourceConnectionConfiguration.GetValue<string>("Type") ?? "api";
    }
    
    public static string GetTargetConnectionType(IConfigurationRoot configurationRoot)
    {
        var connectionsConfiguration = configurationRoot.GetSection("Connections");
        var sourceConnectionConfiguration = connectionsConfiguration.GetSection("Target");

        return sourceConnectionConfiguration.GetValue<string>("Type") ?? "api";
    }
}