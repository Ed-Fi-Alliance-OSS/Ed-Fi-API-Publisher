namespace EdFi.Tools.ApiPublisher.Core.Configuration
{
    public class ConnectionConfiguration
    {
        public Connections Connections { get; set; }
    }
    
    public class Connections
    {
        public NamedConnectionDetailsBase Source { get; set; }
        public NamedConnectionDetailsBase Target { get; set; }
    }
}