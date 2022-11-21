using System;
using Autofac;
using EdFi.Tools.ApiPublisher.Configuration.SqlServer.Modules;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Plugin;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Configuration.SqlServer;

public class Plugin : IPlugin
{
    private const string ConfigurationProviderName = "sqlServer";
        
    public void ApplyConfiguration(string[] args, IConfigurationBuilder configBuilder)
    {
        // Nothing to do
    }

    public void PerformConfigurationRegistrations(ContainerBuilder containerBuilder, IConfigurationRoot initialConfigurationRoot)
    {
        if (initialConfigurationRoot.IsConnectionConfigurationProviderSelected(ConfigurationProviderName))
        {
            containerBuilder.RegisterModule(new PluginModule());
        }
    }

    public void PerformFinalRegistrations(ContainerBuilder containerBuilder, IConfigurationRoot finalConfigurationRoot)
    {
        // Nothing to do 
    }
}
