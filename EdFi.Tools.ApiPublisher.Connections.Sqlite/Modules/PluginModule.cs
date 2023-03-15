using Autofac;
using EdFi.Tools.ApiPublisher.Connections.Sqlite.Configuration;
using EdFi.Tools.ApiPublisher.Core.Configuration;

namespace EdFi.Tools.ApiPublisher.Connections.Sqlite.Modules;

public class PluginModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        // Register the connection configuration details
        builder.RegisterType<SqliteConnectionDetails>()
            .Named<INamedConnectionDetails>(Plugin.SqliteConnectionType);
    }
}
