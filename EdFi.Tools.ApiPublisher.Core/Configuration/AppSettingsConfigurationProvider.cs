using System;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Core.Configuration
{
    public class AppSettingsConfigurationProvider : IAppSettingsConfigurationProvider
    {
        private readonly Lazy<IConfiguration> _configuration;
        
        public AppSettingsConfigurationProvider()
        {
            _configuration = new Lazy<IConfiguration>(() =>
            {
                string environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            
                var configuration = new ConfigurationBuilder()
                    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                    .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
                    .Build();

                return configuration;
            });
        }
        
        public IConfiguration GetConfiguration()
        {
            return _configuration.Value;
        }
    }
}