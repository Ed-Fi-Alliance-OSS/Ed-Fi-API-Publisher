using Autofac;
using EdFi.Tools.ApiPublisher.Core.Management;
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