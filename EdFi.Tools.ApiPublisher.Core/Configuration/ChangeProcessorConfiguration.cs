using System;
using EdFi.Tools.ApiPublisher.Core.ApiClientManagement;
using log4net;
using Microsoft.Extensions.Configuration;
using Version = EdFi.Tools.ApiPublisher.Core.Helpers.Version;

namespace EdFi.Tools.ApiPublisher.Core.Configuration
{
    public class ChangeProcessorConfiguration
    {
        private readonly Lazy<EdFiApiClient> _sourceApiClient;
        private readonly Lazy<EdFiApiClient> _targetApiClient;

        private readonly ILog _logger = LogManager.GetLogger(typeof(ChangeProcessorConfiguration));
        
        public ChangeProcessorConfiguration(
            AuthorizationFailureHandling[] authorizationFailureHandling,
            ApiConnectionDetails sourceApiConnectionDetails,
            ApiConnectionDetails targetApiConnectionDetails,
            Func<EdFiApiClient> sourceApiClientFactory,
            Func<EdFiApiClient> targetApiClientFactory,
            Options options,
            IConfigurationSection configurationStoreSection)
        {
            AuthorizationFailureHandling = authorizationFailureHandling;
            SourceApiConnectionDetails = sourceApiConnectionDetails;
            TargetApiConnectionDetails = targetApiConnectionDetails;
            Options = options;
            ConfigurationStoreSection = configurationStoreSection;

            _sourceApiClient = new Lazy<EdFiApiClient>(() =>
            {
                // Establish connection to source API
                _logger.Info("Initializing source API client...");

                return sourceApiClientFactory();
            });
            
            _targetApiClient = new Lazy<EdFiApiClient>(() =>
            {
                // Establish connection to target API
                _logger.Info("Initializing target API client...");

                return targetApiClientFactory();
            });
        }

        public AuthorizationFailureHandling[] AuthorizationFailureHandling { get; }
        
        public ApiConnectionDetails SourceApiConnectionDetails { get; }
        
        public ApiConnectionDetails TargetApiConnectionDetails { get; }
        
        public EdFiApiClient SourceApiClient
        {
            get => _sourceApiClient.Value;
        }
        
        public EdFiApiClient TargetApiClient
        {
            get => _targetApiClient.Value;
        }
        
        public Options Options { get; }
        public IConfigurationSection ConfigurationStoreSection { get; }

        public Version SourceApiVersion { get; set; }
        public Version TargetApiVersion { get; set; }
    }
}