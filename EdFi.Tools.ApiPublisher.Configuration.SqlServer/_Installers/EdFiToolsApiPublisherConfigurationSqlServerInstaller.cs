using Castle.MicroKernel.Registration;
using Castle.Windsor;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.InversionOfControl;
using EdFi.Tools.ApiPublisher.Core.Management;
using EdFi.Tools.ApiPublisher.Core.Processing;

namespace EdFi.Tools.ApiPublisher.Configuration.SqlServer._Installers
{
    [ApiConnectionsConfigurationSourceName("sql-server")]
    public class EdFiToolsApiPublisherConfigurationSqlServerInstaller : RegistrationMethodsInstallerBase
    {
        protected virtual void RegisterIChangeVersionProcessedWriter(IWindsorContainer container)
        {
            container.Register(
                Component
                    .For<IChangeVersionProcessedWriter>()
                    .ImplementedBy<SqlServerConfigurationChangeVersionProcessedWriter>());
        }

        protected virtual void RegisterINamedApiConnectionDetailsReader(IWindsorContainer container)
        {
            container.Register(
                Component
                    .For<INamedApiConnectionDetailsReader>()
                    .ImplementedBy<SqlServerConfigurationNamedApiConnectionDetailsReader>());
        }
    }
}