using Autofac;
using EdFi.Tools.ApiPublisher.Connections.Sqlite.Modules;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Plugin;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Connections.Sqlite;

public class Plugin : IPlugin
{
    public const string SqliteConnectionType = "sqlite";

    public void ApplyConfiguration(string[] args, IConfigurationBuilder configBuilder)
    {
        configBuilder.AddCommandLine(args, new Dictionary<string, string>
        {
            ["--sourceFile"] = "Connections:Source:File",
            ["--targetFile"] = "Connections:Target:File"
        });
    }

    public void PerformConfigurationRegistrations(ContainerBuilder containerBuilder, IConfigurationRoot initialConfigurationRoot)
    {
        containerBuilder.RegisterModule(new PluginModule());
    }

    public void PerformFinalRegistrations(ContainerBuilder containerBuilder, IConfigurationRoot finalConfigurationRoot)
    {
        // TODO: When support is added for Sqlite database as source  
        string sourceConnectionType = ConfigurationHelper.GetSourceConnectionType(finalConfigurationRoot);
        
        if (sourceConnectionType == SqliteConnectionType)
        {
            containerBuilder.RegisterModule(new SqliteAsSourceModule(finalConfigurationRoot));
        }

        string targetConnectionType = ConfigurationHelper.GetTargetConnectionType(finalConfigurationRoot);

        if (targetConnectionType == SqliteConnectionType)
        {
            containerBuilder.RegisterModule(new SqliteAsTargetModule(finalConfigurationRoot));
        }
    }
}
