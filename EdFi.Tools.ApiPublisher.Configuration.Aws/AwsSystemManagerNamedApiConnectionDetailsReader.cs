using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Management;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Configuration.Aws
{
    public class AwsSystemManagerNamedApiConnectionDetailsReader : INamedApiConnectionDetailsReader
    {
        private readonly IAwsOptionsProvider _awsOptionsProvider;

        public AwsSystemManagerNamedApiConnectionDetailsReader(IAwsOptionsProvider awsOptionsProvider)
        {
            _awsOptionsProvider = awsOptionsProvider;
        }
        
        public ApiConnectionDetails GetNamedApiConnectionDetails(string apiConnectionName)
        {
            // Load named connection information from AWS Systems Manager
            var config = new ConfigurationBuilder()
                .AddSystemsManager($"/ed-fi/publisher/connections/{apiConnectionName}", _awsOptionsProvider.GetOptions())
                .Build();
            
            // Read the connection details from the configuration values
            var connectionDetails = config.Get<ApiConnectionDetails>();

            // Assign the connection name
            connectionDetails.Name = apiConnectionName;
            
            return connectionDetails;
        }
    }
}