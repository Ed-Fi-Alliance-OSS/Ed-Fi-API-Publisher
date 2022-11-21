using Autofac;
using EdFi.Tools.ApiPublisher.Connections.Api.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing;

namespace EdFi.Tools.ApiPublisher.Configuration.Aws.Modules
{
    public class PluginModule : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder.RegisterType<AwsSystemManagerChangeVersionProcessedWriter>()
                .As<IChangeVersionProcessedWriter>()
                .SingleInstance();
            
            builder.RegisterType<AwsSystemManagerNamedApiConnectionDetailsReader>()
                .As<INamedApiConnectionDetailsReader>()
                .SingleInstance();
        }
    }
}