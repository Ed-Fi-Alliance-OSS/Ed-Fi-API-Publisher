using Autofac;
using EdFi.Tools.ApiPublisher.Core.Configuration.Enhancers;
using EdFi.Tools.ApiPublisher.Core.Dependencies;
using EdFi.Tools.ApiPublisher.Core.Processing;

namespace EdFi.Tools.ApiPublisher.Core.Modules
{
    public class EdFiToolsApiPublisherCoreModule : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder.RegisterType<NamedConnectionsConfigurationBuilderEnhancer>()
                .As<IConfigurationBuilderEnhancer>()
                .SingleInstance();

            builder.RegisterType<EdFiV3ApiResourceDependencyProvider>()
                .As<IResourceDependencyProvider>()
                .SingleInstance();

            builder.RegisterType<SerilogErrorPublisher>()
                .As<IErrorPublisher>()
                .SingleInstance();
        }
    }
}