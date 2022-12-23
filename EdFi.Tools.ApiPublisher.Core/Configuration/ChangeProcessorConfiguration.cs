using System;
using EdFi.Tools.ApiPublisher.Core.ApiClientManagement;
using log4net;
using Microsoft.Extensions.Configuration;
using Version = EdFi.Tools.ApiPublisher.Core.Helpers.Version;

namespace EdFi.Tools.ApiPublisher.Core.Configuration
{
    public class ChangeProcessorConfiguration
    {
        private readonly ILog _logger = LogManager.GetLogger(typeof(ChangeProcessorConfiguration));
        
        public ChangeProcessorConfiguration(
            Options options,
            AuthorizationFailureHandling[] authorizationFailureHandling,
            string[] resourcesWithUpdatableKeys,
            IConfigurationSection configurationStoreSection,
            Func<string>? javascriptModuleFactory = null)
        {
            AuthorizationFailureHandling = authorizationFailureHandling;
            ResourcesWithUpdatableKeys = resourcesWithUpdatableKeys;
            JavascriptModuleFactory = javascriptModuleFactory;
            
            Options = options;
            ConfigurationStoreSection = configurationStoreSection;
        }

        public AuthorizationFailureHandling[] AuthorizationFailureHandling { get; }
        public string[] ResourcesWithUpdatableKeys { get; }

        public Func<string>? JavascriptModuleFactory { get; }

        public Options Options { get; }
        public IConfigurationSection ConfigurationStoreSection { get; }

        // NOTE: These seem misplaced here -- could perhaps be moved to a separate context object related to processing 
        public Version? SourceApiVersion { get; set; }
        public Version? TargetApiVersion { get; set; }
    }
}