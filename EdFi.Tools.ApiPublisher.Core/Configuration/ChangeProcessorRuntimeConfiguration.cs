using System;
using EdFi.Tools.ApiPublisher.Core.ApiClientManagement;
using log4net;

namespace EdFi.Tools.ApiPublisher.Core.Configuration
{
    public class ChangeProcessorRuntimeConfiguration
    {
        private readonly Lazy<EdFiApiClient> _sourceApiClient;
        private readonly Lazy<EdFiApiClient> _targetApiClient;

        private readonly ILog _logger = LogManager.GetLogger(typeof(ChangeProcessorRuntimeConfiguration));
        
        public ChangeProcessorRuntimeConfiguration(
            string[] commandLineArgs,
            AuthorizationFailureHandling[] authorizationFailureHandling,
            ApiConnectionDetails sourceApiConnectionDetails,
            ApiConnectionDetails targetApiConnectionDetails,
            Func<EdFiApiClient> sourceApiClientFactory,
            Func<EdFiApiClient> targetApiClientFactory,
            Options options)
        {
            CommandLineArgs = commandLineArgs;
            AuthorizationFailureHandling = authorizationFailureHandling;
            SourceApiConnectionDetails = sourceApiConnectionDetails;
            TargetApiConnectionDetails = targetApiConnectionDetails;
            Options = options;
            
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
        
        public string[] CommandLineArgs { get; set; }
    }
}