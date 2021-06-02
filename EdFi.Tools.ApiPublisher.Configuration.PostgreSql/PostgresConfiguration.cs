using EdFi.Tools.ApiPublisher.Core._Installers;

namespace EdFi.Tools.ApiPublisher.Configuration.PostgreSql
{
    public class PostgresConfigurationStore
    {
        public PostgresConfiguration PostgreSql { get; set; }
    }

    public class PostgresConfiguration 
    {
        public string ConnectionString { get; set; }
        public string EncryptionPassword { get; set; }
    }
}