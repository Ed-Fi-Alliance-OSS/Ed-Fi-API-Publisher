using System;
using System.Threading.Tasks;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using EdFi.Tools.ApiPublisher.Core.Processing;
using log4net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Configuration.Aws
{
    public class AwsSystemManagerChangeVersionProcessedWriter : IChangeVersionProcessedWriter
    {
        private readonly IAmazonSimpleSystemsManagement _amazonSimpleSystemsManagement;

        private readonly ILog _logger = LogManager.GetLogger(typeof(AwsSystemManagerChangeVersionProcessedWriter));
        
        public AwsSystemManagerChangeVersionProcessedWriter(IAwsOptionsProvider awsOptionsProvider)
        {
            _amazonSimpleSystemsManagement = awsOptionsProvider.GetOptions().CreateServiceClient<IAmazonSimpleSystemsManagement>();
        }
        
        public async Task SetProcessedChangeVersionAsync(string sourceConnectionName, string targetConnectionName, long changeVersion)
        {
            var currentParameter = await GetParameterValueAsync(sourceConnectionName)
                .ConfigureAwait(false);

            // Assign the new "LastChangeVersionProcessed" value
            currentParameter[targetConnectionName] = changeVersion;
            
            // Serialize the parameter's values
            string newParameterJson = currentParameter.ToString(Formatting.None);

            string parameterName = $"/ed-fi/publisher/connections/{sourceConnectionName}/lastChangeVersionsProcessed";

            var putRequest = new PutParameterRequest
            {
                Type = ParameterType.String,
                Name = parameterName,
                Value = newParameterJson,
                Overwrite = true,
            };

            var response = await _amazonSimpleSystemsManagement.PutParameterAsync(putRequest)
                .ConfigureAwait(false);

            if ((int) response.HttpStatusCode >= 400)
            {
                throw new Exception(
                    $"Failed to write updated change version of {changeVersion} for source connection '{sourceConnectionName}' to target connection '{targetConnectionName}' (AWS response status: {response.HttpStatusCode}).");
            }
        }

        private async Task<JObject> GetParameterValueAsync(string sourceConnectionName)
        {
            string parameterName = $"/ed-fi/publisher/connections/{sourceConnectionName}/lastChangeVersionsProcessed";
            
            var getRequest = new GetParameterRequest
            {
                Name = parameterName,
            };

            GetParameterResponse getResponse = null;

            try
            {
                getResponse = await _amazonSimpleSystemsManagement.GetParameterAsync(getRequest).ConfigureAwait(false);
            }
            catch (ParameterNotFoundException)
            {
                _logger.Debug(
                    $"AWS Parameter Store parameter '{parameterName}' not found. A new parameter will be created.");

                return new JObject();
            }
            catch (Exception ex)
            {
                throw new Exception(
                    $"Unable to read parameter '{parameterName}' from AWS Systems Manager Parameter Store.", ex);
            }

            string json = getResponse.Parameter.Value;

            return JObject.Parse(string.IsNullOrEmpty(json) ? "{}" : json);
        }
    }
}