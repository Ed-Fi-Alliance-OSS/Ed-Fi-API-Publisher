namespace EdFi.Tools.ApiPublisher.Configuration.SqlServer
{
    public class SqlServerConfigurationStore
    {
        public SqlServerConfiguration SqlServer { get; set; }
    }
    
    public class SqlServerConfiguration
    {
        public string ConnectionString { get; set; }
    }
}