namespace EdFi.Tools.ApiPublisher.ConfigurationStore.SqlServer
{
    public class SqlServerConfigurationStore
    {
        public SqlServerConfiguration? SqlServer { get; set; }
    }
    
    public class SqlServerConfiguration
    {
        public string? ConnectionString { get; set; }
    }
}