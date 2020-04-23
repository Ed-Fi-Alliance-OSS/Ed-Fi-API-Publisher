using System;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Configuration.SqlServer
{
    public class SqlServerConfigurationSource : IConfigurationSource
    {
        public string ConfigurationKey { get; }
        public string ConnectionString { get; }

        public SqlServerConfigurationSource(string configurationKey, string connectionString)
        {
            // Ensure the stored-prefix includes the key separator
            ConfigurationKey = configurationKey.TrimEnd('/') + '/';
            ConnectionString = connectionString;
        }
        
        public IConfigurationProvider Build(IConfigurationBuilder builder)
        {
            return new SqlServerConfigurationProvider(this);
        }
    }
}