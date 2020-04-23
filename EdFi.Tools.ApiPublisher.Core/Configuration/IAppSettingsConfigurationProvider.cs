using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Core.Configuration
{
    public interface IAppSettingsConfigurationProvider
    {
        IConfiguration GetConfiguration();
    }
}