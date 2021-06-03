using System;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Management;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Configuration.SqlServer
{
    public class SqlServerConfigurationNamedApiConnectionDetailsReader : INamedApiConnectionDetailsReader
    {
        public ApiConnectionDetails GetNamedApiConnectionDetails(
            string apiConnectionName,
            IConfigurationSection configurationStoreSection)
        {
            var sqlServerConfiguration = configurationStoreSection.Get<SqlServerConfigurationStore>().SqlServer;

            // Load named connection information from SQL Server configuration store
            var config = new ConfigurationBuilder()
                .AddConfigurationStoreForSqlServer(ConfigurationStoreHelper.Key(apiConnectionName), sqlServerConfiguration.ConnectionString)
                .Build();

            // Read the connection details from the configuration values
            var connectionDetails = config.Get<ApiConnectionDetails>();

            // Assign the connection name
            connectionDetails.Name = apiConnectionName;

            return connectionDetails;
        }
    }
}