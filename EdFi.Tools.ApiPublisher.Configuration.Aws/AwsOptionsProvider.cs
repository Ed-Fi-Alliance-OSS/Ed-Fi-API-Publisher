using System;
using Amazon.Extensions.NETCore.Setup;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Configuration.Aws
{
    public class AwsOptionsProvider : IAwsOptionsProvider
    {
        private readonly Lazy<AWSOptions> _awsOptions;

        public AwsOptionsProvider(IAppSettingsConfigurationProvider appSettingsConfigurationProvider)
        {
            _awsOptions = new Lazy<AWSOptions>(() =>
            {
                var configuration = appSettingsConfigurationProvider.GetConfiguration();
                var awsOptions = configuration.GetAWSOptions();

                return awsOptions;
            });
        }

        public AWSOptions GetOptions()
        {
            return _awsOptions.Value;
        }
    }
}