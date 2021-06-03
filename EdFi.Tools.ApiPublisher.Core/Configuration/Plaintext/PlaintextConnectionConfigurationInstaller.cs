using Castle.MicroKernel.Registration;
using Castle.Windsor;
using EdFi.Tools.ApiPublisher.Core.InversionOfControl;
using EdFi.Tools.ApiPublisher.Core.Management;
using EdFi.Tools.ApiPublisher.Core.Processing;

namespace EdFi.Tools.ApiPublisher.Core.Configuration.Plaintext
{
    [ApiConnectionsConfigurationSourceName("plainText")]
    public class PlaintextConnectionConfigurationInstaller : RegistrationMethodsInstallerBase
    {
        protected virtual void RegisterIChangeVersionProcessedWriter(IWindsorContainer container)
        {
            container.Register(
                Component
                    .For<IChangeVersionProcessedWriter>()
                    .ImplementedBy<PlaintextChangeVersionProcessedWriter>());
        }

        protected virtual void RegisterINamedApiConnectionDetailsReader(IWindsorContainer container)
        {
            container.Register(
                Component
                    .For<INamedApiConnectionDetailsReader>()
                    .ImplementedBy<PlainTextJsonFileNamedApiConnectionDetailsReader>());
        }
    }
}