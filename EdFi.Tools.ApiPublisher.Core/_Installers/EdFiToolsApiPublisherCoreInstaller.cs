using Castle.MicroKernel.Registration;
using Castle.Windsor;
using EdFi.Tools.ApiPublisher.Core.Management;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Dependencies;
using EdFi.Tools.ApiPublisher.Core.InversionOfControl;
using EdFi.Tools.ApiPublisher.Core.Processing;

namespace EdFi.Tools.ApiPublisher.Core._Installers
{
    public class EdFiToolsApiPublisherCoreInstaller : RegistrationMethodsInstallerBase
    {
        protected virtual void RegisterIChangeProcessorConfigurationProvider(IWindsorContainer container)
        {
            container.Register(
                Component
                    .For<IChangeProcessorConfigurationProvider>()
                    .ImplementedBy<ChangeProcessorConfigurationProvider>());
        }
        
        protected virtual void RegisterIChangeProcessor(IWindsorContainer container)
        {
            container.Register(
                Component
                    .For<IChangeProcessor>()
                    .ImplementedBy<ChangeProcessor>());
        }
        
        protected virtual void RegisterIEdFiPublisherConfigurationProvider(IWindsorContainer container)
        {
            container.Register(
                Component
                    .For<IEdFiApiPublisherConfigurationProvider>()
                    .ImplementedBy<EdFiApiPublisherConfigurationProvider>()
                );
        }

        protected virtual void RegisterIResourceDependencyProvider(IWindsorContainer container)
        {
            container.Register(
                Component
                    .For<IResourceDependencyProvider>()
                    .ImplementedBy<EdFiV3ApiResourceDependencyProvider>());
        }

        protected virtual void RegisterIErrorPublisher(IWindsorContainer container)
        {
            container.Register(
                Component
                    .For<IErrorPublisher>()
                    .ImplementedBy<Log4NetErrorPublisher>());
        }

        protected virtual void RegisterIAppSettingsConfigurationProvider(IWindsorContainer container)
        {
            container.Register(
                Component
                    .For<IAppSettingsConfigurationProvider>()
                    .ImplementedBy<AppSettingsConfigurationProvider>());
        }
    }
}