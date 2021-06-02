using EdFi.Tools.ApiPublisher.Core.Configuration;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Core.Management
{
    public interface INamedApiConnectionDetailsReader
    {
        ApiConnectionDetails GetNamedApiConnectionDetails(
            string apiConnectionName,
            IConfigurationSection configurationStoreSection);
    }
}