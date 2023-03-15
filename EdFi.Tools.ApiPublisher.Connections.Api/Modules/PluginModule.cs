using Autofac;
using EdFi.Tools.ApiPublisher.Connections.Api.Configuration;
using EdFi.Tools.ApiPublisher.Connections.Api.Configuration.Enhancers;
using EdFi.Tools.ApiPublisher.Connections.Api.DependencyResolution;
using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Blocks;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Configuration.Enhancers;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Modules;

public class PluginModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<ApiConnectionDetails>()
            .Named<INamedConnectionDetails>(Plugin.ApiConnectionType);
        
        builder.RegisterType<EdFiApiConnectionsConfigurationBuilderEnhancer>()
            .As<IConfigurationBuilderEnhancer>()
            .SingleInstance();

        builder.RegisterType<FallbackSourceResourceItemProvider>()
            .As<ISourceResourceItemProvider>()
            .SingleInstance();
    }
}