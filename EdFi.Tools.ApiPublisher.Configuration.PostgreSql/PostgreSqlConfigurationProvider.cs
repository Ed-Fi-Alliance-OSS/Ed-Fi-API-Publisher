using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Configuration.PostgreSql
{
    public class PostgreSqlConfigurationProvider : ConfigurationProvider
    {
        private readonly PostgreSqlConfigurationSource _postgreSqlConfigurationSource;

        public PostgreSqlConfigurationProvider(PostgreSqlConfigurationSource postgreSqlConfigurationSource)
        {
            _postgreSqlConfigurationSource = postgreSqlConfigurationSource;
        }

        public override void Load()
        {
            var settings = new PostgreSqlConfigurationValuesProvider()
                .GetConfigurationValues(
                    _postgreSqlConfigurationSource.ConnectionString, 
                    _postgreSqlConfigurationSource.EncryptionPassword, 
                    _postgreSqlConfigurationSource.ConfigurationKeyPrefix);

            Data = settings;
        }
    }
}