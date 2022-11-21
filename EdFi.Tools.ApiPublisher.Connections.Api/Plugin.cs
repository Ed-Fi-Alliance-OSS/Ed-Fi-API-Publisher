using Autofac;
using EdFi.Tools.ApiPublisher.Connections.Api.Modules;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Plugin;
using EdFi.Tools.ApiPublisher.Core.Versioning;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Connections.Api;

public class Plugin : IPlugin
{
    public const string ApiConnectionType = "api";

    public void ApplyConfiguration(string[] args, IConfigurationBuilder configBuilder)
    {
        // Nothing to do
    }

    public void PerformConfigurationRegistrations(ContainerBuilder containerBuilder, IConfigurationRoot initialConfigurationRoot)
    {
        containerBuilder.RegisterModule(new EdFiApiModule());
    }

    public void PerformFinalRegistrations(ContainerBuilder containerBuilder, IConfigurationRoot finalConfigurationRoot)
    {
        string sourceConnectionType = ConfigurationHelper.GetSourceConnectionType(finalConfigurationRoot);

        if (sourceConnectionType == ApiConnectionType)
        {
            containerBuilder.RegisterModule(new EdFiApiAsSourceModule(finalConfigurationRoot));
        }

        string targetConnectionType = ConfigurationHelper.GetTargetConnectionType(finalConfigurationRoot);

        if (targetConnectionType == ApiConnectionType)
        {
            containerBuilder.RegisterModule(new EdFiApiAsTargetModule(finalConfigurationRoot));
        }
    }
}
