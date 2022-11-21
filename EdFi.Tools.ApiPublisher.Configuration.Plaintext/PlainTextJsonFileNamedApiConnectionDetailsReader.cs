using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Management;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Configuration.Plaintext
{
    public class PlainTextJsonFileNamedApiConnectionDetailsReader : INamedApiConnectionDetailsReader
    {
        public ApiConnectionDetails GetNamedApiConnectionDetails(
            string apiConnectionName,
            IConfigurationSection configurationStoreSection)
        {
            // Build the configuration
            var config = new ConfigurationBuilder()
                .AddJsonFile("plainTextNamedConnections.json")
                .Build();
            
            var connections = config.Get<PlainTextNamedConnectionConfiguration>();
            
            return connections.Connections
                    .Where(details => details.Name != null)
                    .FirstOrDefault(details => details.Name!.Equals(apiConnectionName, StringComparison.OrdinalIgnoreCase))
                ?? new ApiConnectionDetails { Name = apiConnectionName };
        }
    }
}