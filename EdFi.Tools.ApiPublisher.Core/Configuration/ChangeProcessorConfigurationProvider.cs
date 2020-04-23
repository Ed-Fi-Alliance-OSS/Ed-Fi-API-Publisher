using System;
using EdFi.Tools.ApiPublisher.Core.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Processing;
using log4net;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Core.Configuration
{
    public class ChangeProcessorConfigurationProvider : IChangeProcessorConfigurationProvider
    {
        private readonly IEdFiApiPublisherConfigurationProvider _configurationProvider;

        private readonly ILog _logger = LogManager.GetLogger(typeof(ChangeProcessorConfigurationProvider));

        public ChangeProcessorConfigurationProvider(IEdFiApiPublisherConfigurationProvider configurationProvider)
        {
            _configurationProvider = configurationProvider;
        }

        public ChangeProcessorRuntimeConfiguration GetRuntimeConfiguration(string[] commandLineArgs)
        {
            try
            {
                var configuration = _configurationProvider.GetConfiguration(commandLineArgs);

                var publisherSettings = configuration.Get<PublisherSettings>();
                var options = publisherSettings.Options;
                var authorizationFailureHandling = publisherSettings.AuthorizationFailureHandling;

                var apiConnections = configuration.Get<ConnectionConfiguration>().Connections;

                var sourceApiConnectionDetails = apiConnections.Source;
                var targetApiConnectionDetails = apiConnections.Target;

                EdFiApiClient CreateSourceApiClient() => new EdFiApiClient(sourceApiConnectionDetails, options.BearerTokenRefreshMinutes, options.IgnoreSSLErrors);
                EdFiApiClient CreateTargetApiClient() => new EdFiApiClient(targetApiConnectionDetails, options.BearerTokenRefreshMinutes, options.IgnoreSSLErrors);

                return new ChangeProcessorRuntimeConfiguration(
                    commandLineArgs,
                    authorizationFailureHandling,
                    sourceApiConnectionDetails,
                    targetApiConnectionDetails,
                    CreateSourceApiClient,
                    CreateTargetApiClient,
                    options);
            }
            catch (Exception ex)
            {
                _logger.Error($"An unexpected error occurred during configuration: {ex}");
                throw;
            }
        }
    }
}