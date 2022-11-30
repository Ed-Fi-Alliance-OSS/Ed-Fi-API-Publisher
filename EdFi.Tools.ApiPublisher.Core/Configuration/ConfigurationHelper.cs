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