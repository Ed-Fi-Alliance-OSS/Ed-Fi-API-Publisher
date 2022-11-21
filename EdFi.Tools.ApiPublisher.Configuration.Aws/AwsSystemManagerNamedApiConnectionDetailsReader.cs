using EdFi.Tools.ApiPublisher.Connections.Api.Configuration;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Configuration.Aws
{
    public class AwsSystemManagerNamedApiConnectionDetailsReader : INamedApiConnectionDetailsReader
    {
        public ApiConnectionDetails GetNamedApiConnectionDetails(
            string apiConnectionName,
            IConfigurationSection configurationStoreSection)
        {
            var awsOptions = configurationStoreSection.GetAWSOptions("awsParameterStore");

            // Load named connection information from AWS Systems Manager
            var config = new ConfigurationBuilder()
                .AddSystemsManager(ConfigurationStoreHelper.Key(apiConnectionName), awsOptions)
                .Build();
            
            // Read the connection details from the configuration values
            var connectionDetails = config.Get<ApiConnectionDetails>();

            // Assign the connection name
            connectionDetails.Name = apiConnectionName;
            
            return connectionDetails;
        }
    }
}