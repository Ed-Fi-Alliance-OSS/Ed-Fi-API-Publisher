using Castle.MicroKernel.Registration;
using Castle.Windsor;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.InversionOfControl;
using EdFi.Tools.ApiPublisher.Core.Management;
using EdFi.Tools.ApiPublisher.Core.Processing;

namespace EdFi.Tools.ApiPublisher.Configuration.PostgreSql._Installers
{
    [ApiConnectionsConfigurationSourceName("postgreSql")]
    public class EdFiToolsApiPublisherConfigurationPostgreSqlInstaller : RegistrationMethodsInstallerBase
    {
        protected virtual void RegisterIChangeVersionProcessedWriter(IWindsorContainer container)
        {
            container.Register(
                Component
                    .For<IChangeVersionProcessedWriter>()
                    .ImplementedBy<PostgreSqlConfigurationChangeVersionProcessedWriter>());
        }

        protected virtual void RegisterINamedApiConnectionDetailsReader(IWindsorContainer container)
        {
            container.Register(
                Component
                    .For<INamedApiConnectionDetailsReader>()
                    .ImplementedBy<PostgreSqlConfigurationNamedApiConnectionDetailsReader>());
        }
    }
}