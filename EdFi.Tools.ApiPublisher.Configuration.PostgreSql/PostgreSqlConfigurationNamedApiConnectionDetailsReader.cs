using System;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Management;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Configuration.PostgreSql
{
    public class PostgreSqlConfigurationNamedApiConnectionDetailsReader : INamedApiConnectionDetailsReader
    {
        public ApiConnectionDetails GetNamedApiConnectionDetails(
            string apiConnectionName,
            IConfigurationSection configurationStoreSection)
        {
            var postgresConfiguration = configurationStoreSection.Get<PostgresConfigurationStore>().PostgreSql;
            
            if (string.IsNullOrWhiteSpace(postgresConfiguration?.EncryptionPassword)) 
            {
                throw new Exception("The PostgreSQL Configuration Store encryption key for storing API keys and secrets was not provided.");
            }
            
            // Load named connection information from PostgreSQL configuration store
            var config = new ConfigurationBuilder()
                .AddConfigurationStoreForPostgreSql(
                    ConfigurationStoreHelper.Key(apiConnectionName),
                    postgresConfiguration.ConnectionString,
                    postgresConfiguration.EncryptionPassword)
                .Build();

            // Read the connection details from the configuration values
            var connectionDetails = config.Get<ApiConnectionDetails>();

            // Assign the connection name
            connectionDetails.Name = apiConnectionName;

            return connectionDetails;
        }
    }
}