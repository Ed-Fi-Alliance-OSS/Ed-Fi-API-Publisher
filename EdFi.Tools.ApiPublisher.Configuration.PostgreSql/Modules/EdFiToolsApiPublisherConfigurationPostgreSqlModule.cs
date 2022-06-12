using Autofac;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Management;
using EdFi.Tools.ApiPublisher.Core.Processing;

namespace EdFi.Tools.ApiPublisher.Configuration.PostgreSql.Modules
{
    [ApiConnectionsConfigurationSourceName("postgreSql")]
    public class EdFiToolsApiPublisherConfigurationPostgreSqlModule : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder.RegisterType<PostgreSqlConfigurationChangeVersionProcessedWriter>()
                .As<IChangeVersionProcessedWriter>()
                .SingleInstance();
            
            builder.RegisterType<PostgreSqlConfigurationNamedApiConnectionDetailsReader>()
                .As<INamedApiConnectionDetailsReader>()
                .SingleInstance();
        }
    }
}