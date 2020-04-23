using EdFi.Tools.ApiPublisher.Core.Configuration;

namespace EdFi.Tools.ApiPublisher.Core.Management
{
    public interface INamedApiConnectionDetailsReader
    {
        ApiConnectionDetails GetNamedApiConnectionDetails(string apiConnectionName);
    }
}