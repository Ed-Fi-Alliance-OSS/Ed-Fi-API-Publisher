using System;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Core.Configuration;

public static class ConfigurationExtensions
{
    public static bool IsConnectionConfigurationProviderSelected(this IConfigurationRoot configurationRoot, string configurationProviderName)
    {
        return string.Equals(
            configurationProviderName,
            ConfigurationHelper.GetConfigurationStoreProviderName(configurationRoot),
            StringComparison.OrdinalIgnoreCase);
    }
}
