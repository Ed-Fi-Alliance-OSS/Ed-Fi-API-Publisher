// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using System;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.ConfigurationStore.Aws
{
	public class AwsSystemManagerChangeVersionProcessedWriter : IChangeVersionProcessedWriter
    {
        private readonly ILogger _logger = Log.ForContext(typeof(AwsSystemManagerChangeVersionProcessedWriter));
        
        public async Task SetProcessedChangeVersionAsync(
            string sourceConnectionName,
            string targetConnectionName,
            long changeVersion,
            IConfigurationSection configurationStoreSection)
        {
            var awsOptions = configurationStoreSection.GetAWSOptions("awsParameterStore");
            var amazonSimpleSystemsManagement = awsOptions.CreateServiceClient<IAmazonSimpleSystemsManagement>();

            var currentParameter = await GetParameterValueAsync(amazonSimpleSystemsManagement, sourceConnectionName)
                .ConfigureAwait(false);

            // Assign the new "LastChangeVersionProcessed" value
            currentParameter[targetConnectionName] = changeVersion;
            
            // Serialize the parameter's values
            string newParameterJson = currentParameter.ToString(Formatting.None);

            string parameterName = $"{ConfigurationStoreHelper.Key(sourceConnectionName)}/lastChangeVersionsProcessed";

            var putRequest = new PutParameterRequest
            {
                Type = ParameterType.String,
                Name = parameterName,
                Value = newParameterJson,
                Overwrite = true,
            };

            var response = await amazonSimpleSystemsManagement.PutParameterAsync(putRequest)
                .ConfigureAwait(false);

            if ((int) response.HttpStatusCode >= 400)
            {
                throw new Exception(
                    $"Failed to write updated change version of {changeVersion} for source connection '{sourceConnectionName}' to target connection '{targetConnectionName}' (AWS response status: {response.HttpStatusCode}).");
            }
        }

        private async Task<JObject> GetParameterValueAsync(
            IAmazonSimpleSystemsManagement amazonSimpleSystemsManagement,
            string sourceConnectionName)
        {
            string parameterName = $"{ConfigurationStoreHelper.Key(sourceConnectionName)}/lastChangeVersionsProcessed";

            var getRequest = new GetParameterRequest
            {
                Name = parameterName,
            };

            GetParameterResponse getResponse;

            try
            {
                getResponse = await amazonSimpleSystemsManagement.GetParameterAsync(getRequest).ConfigureAwait(false);
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
