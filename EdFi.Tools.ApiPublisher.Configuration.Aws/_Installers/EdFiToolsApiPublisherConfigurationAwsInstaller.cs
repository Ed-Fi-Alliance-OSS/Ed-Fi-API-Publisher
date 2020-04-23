using Castle.MicroKernel.Registration;
using Castle.Windsor;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.InversionOfControl;
using EdFi.Tools.ApiPublisher.Core.Management;
using EdFi.Tools.ApiPublisher.Core.Processing;

namespace EdFi.Tools.ApiPublisher.Configuration.Aws._Installers
{
    [ApiConnectionsConfigurationSourceName("aws-parameter-store")]

    public class EdFiToolsApiPublisherConfigurationAwsInstaller : RegistrationMethodsInstallerBase
    {
        protected virtual void RegisterIAwsOptionsProvider(IWindsorContainer container)
        {
            container.Register(
                Component
                    .For<IAwsOptionsProvider>()
                    .ImplementedBy<AwsOptionsProvider>());
        }

        protected virtual void RegisterIChangeVersionProcessedWriter(IWindsorContainer container)
        {
            container.Register(
                Component
                    .For<IChangeVersionProcessedWriter>()
                    .ImplementedBy<AwsSystemManagerChangeVersionProcessedWriter>());
        }

        protected virtual void RegisterINamedApiConnectionDetailsReader(IWindsorContainer container)
        {
            container.Register(
                Component
                    .For<INamedApiConnectionDetailsReader>()
                    .ImplementedBy<AwsSystemManagerNamedApiConnectionDetailsReader>());
        }
    }
}