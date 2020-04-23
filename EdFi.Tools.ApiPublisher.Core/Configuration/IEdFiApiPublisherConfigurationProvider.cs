using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Core.Configuration
{
    public interface IEdFiApiPublisherConfigurationProvider
    {
        IConfiguration GetConfiguration(string[] commandLineArgs);
    }
}