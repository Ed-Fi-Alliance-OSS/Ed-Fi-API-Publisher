using Autofac;
using EdFi.Tools.ApiPublisher.Core.Configuration.Enhancers;
using EdFi.Tools.ApiPublisher.Core.Dependencies;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Blocks;
using EdFi.Tools.ApiPublisher.Core.Versioning;

namespace EdFi.Tools.ApiPublisher.Core.Modules
{
    public class CoreModule : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder.RegisterType<ResourceDependencyProvider>()
                .As<IResourceDependencyProvider>()
                .SingleInstance();

            builder.RegisterType<Log4NetErrorPublisher>()
                .As<IErrorPublisher>()
                .SingleInstance();
            
            // General purpose version checker
            builder.RegisterType<EdFiVersionsChecker>()
                .As<IEdFiVersionsChecker>()
                .SingleInstance();
            
            // Block factories
            builder.RegisterType<StreamResourceBlockFactory>(); //.SingleInstance();
            builder.RegisterType<StreamResourcePagesBlockFactory>(); //.SingleInstance();
            builder.RegisterType<PublishErrorsBlocksFactory>(); //.SingleInstance();

            builder.RegisterType<ChangeProcessor>();
        }
    }
}