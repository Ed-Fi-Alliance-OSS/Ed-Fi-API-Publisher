namespace EdFi.Tools.ApiPublisher.ConfigurationStore.PostgreSql
{
    public class PostgresConfigurationStore
    {
        public PostgresConfiguration? PostgreSql { get; set; }
    }

    public class PostgresConfiguration 
    {
        public string? ConnectionString { get; set; }
        public string? EncryptionPassword { get; set; }
    }
}