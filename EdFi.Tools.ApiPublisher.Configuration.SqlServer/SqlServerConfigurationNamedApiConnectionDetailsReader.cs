using System;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Management;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Configuration.SqlServer
{
    public class SqlServerConfigurationNamedApiConnectionDetailsReader : INamedApiConnectionDetailsReader
    {
        private readonly Lazy<string> _connectionString;
        
        public SqlServerConfigurationNamedApiConnectionDetailsReader(IAppSettingsConfigurationProvider appSettingsConfigurationProvider)
        {
            _connectionString = new Lazy<string>(
                () =>
                {
                    var configuration = appSettingsConfigurationProvider.GetConfiguration();
                    return configuration.GetConnectionString("SqlServerConfiguration");
                });
        }
        
        public ApiConnectionDetails GetNamedApiConnectionDetails(string apiConnectionName)
        {
            // Load named connection information from AWS Systems Manager
            var config = new ConfigurationBuilder()
                .AddSqlServerConfiguration($"/ed-fi/publisher/connections/{apiConnectionName}", _connectionString.Value)
                .Build();

            // Read the connection details from the configuration values
            var connectionDetails = config.Get<ApiConnectionDetails>();

            // Assign the connection name
            connectionDetails.Name = apiConnectionName;

            return connectionDetails;
        }
    }
}