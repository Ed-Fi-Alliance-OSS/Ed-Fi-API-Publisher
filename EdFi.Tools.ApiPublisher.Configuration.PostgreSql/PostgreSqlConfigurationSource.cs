using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Configuration.PostgreSql
{
    public class PostgreSqlConfigurationSource : IConfigurationSource
    {
        public string ConfigurationKeyPrefix { get; }
        public string? ConnectionString { get; }
        public string? EncryptionPassword { get; set; }

        public PostgreSqlConfigurationSource(string configurationKeyPrefix, string? connectionString, string? encryptionPassword)
        {
            // Ensure the stored-prefix includes the key separator
            ConfigurationKeyPrefix = configurationKeyPrefix.TrimEnd('/') + '/';
            ConnectionString = connectionString;
            EncryptionPassword = encryptionPassword;
        }
        
        public IConfigurationProvider Build(IConfigurationBuilder builder)
        {
            return new PostgreSqlConfigurationProvider(this);
        }
    }
}