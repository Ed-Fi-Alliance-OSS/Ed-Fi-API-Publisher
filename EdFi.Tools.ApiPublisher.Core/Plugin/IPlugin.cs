using Autofac;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Core.Plugin;

public interface IPlugin
{
    void ApplyConfiguration(string[] args, IConfigurationBuilder configBuilder);
    
    void PerformConfigurationRegistrations(ContainerBuilder containerBuilder, IConfigurationRoot initialConfigurationRoot);
    
    void PerformFinalRegistrations(ContainerBuilder containerBuilder, IConfigurationRoot finalConfigurationRoot);
}
