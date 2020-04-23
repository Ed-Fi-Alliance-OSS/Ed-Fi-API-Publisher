using System;
using System.Collections.Generic;
using System.Linq;
using EdFi.Tools.ApiPublisher.Core.Management;
using log4net;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Core.Configuration
{
    public class EdFiApiPublisherConfigurationProvider : IEdFiApiPublisherConfigurationProvider
    {
        private readonly ILog _logger = LogManager.GetLogger(typeof(EdFiApiPublisherConfigurationProvider));
        
        // Optional dependency injected by Windsor
        public INamedApiConnectionDetailsReader NamedApiConnectionDetailsReader { get; set; }

        public IConfiguration GetConfiguration(string[] commandLineArgs)
        {
            var configBuilder = new ConfigurationBuilder()
                .AddJsonFile("publisherSettings.json")
                .AddEnvironmentVariables("EdFi:Publisher:")
                .AddCommandLine(commandLineArgs, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    // API connections configuration
                    ["--sourceName"] = "Connections:Source:Name",
                    ["--sourceUrl"] = "Connections:Source:Url",
                    ["--sourceKey"] = "Connections:Source:Key",
                    ["--sourceSecret"] = "Connections:Source:Secret",
                    ["--sourceScope"] = "Connections:Source:Scope",
                    ["--lastChangeVersionProcessed"] = "Connections:Source:LastChangeVersionProcessed",
                    ["--targetName"] = "Connections:Target:Name",
                    ["--targetUrl"] = "Connections:Target:Url",
                    ["--targetKey"] = "Connections:Target:Key",
                    ["--targetSecret"] = "Connections:Target:Secret",
                    ["--targetScope"] = "Connections:Target:Scope",
                    
                    // Publisher Options
                    ["--bearerTokenRefreshMinutes"] = "Options:BearerTokenRefreshMinutes",
                    ["--retryStartingDelayMilliseconds"] = "Options:RetryStartingDelayMilliseconds",
                    ["--maxRetryAttempts"] = "Options:MaxRetryAttempts",
                    ["--maxDegreeOfParallelismForPostResourceItem"] = "Options:MaxDegreeOfParallelismForPostResourceItem",
                    ["--maxDegreeOfParallelismForStreamResourcePages"] = "Options:MaxDegreeOfParallelismForStreamResourcePages",
                    ["--streamingPagesWaitDurationSeconds"] = "Options:StreamingPagesWaitDurationSeconds",
                    ["--streamingPageSize"] = "Options:StreamingPageSize",
                    ["--includeDescriptors"] = "Options:IncludeDescriptors",
                    ["--errorPublishingBatchSize"] = "Options:ErrorPublishingBatchSize",
                    ["--ignoreSslErrors"] = "Options:IgnoreSslErrors",
                    ["--whatIf"] = "Options:WhatIf",
                    
                    // Resource selection (comma delimited paths - e.g. "/ed-fi/students,/ed-fi/studentSchoolAssociations")
                    ["--resources"] = "Connections:Source:Resources",
                    ["--treatForbiddenPostAsWarning"] = "Connections:Target:TreatForbiddenPostAsWarning",
                    ["--force"] = "Connections:Source:Force",
                });

            // Build the current configuration
            var configuration = configBuilder.Build();

            // Check connection configurations
            var connections = configuration.Get<ConnectionConfiguration>().Connections;

            // If source and target connections are fully defined, we're done
            if (connections.Source.IsFullyDefined() && connections.Target.IsFullyDefined())
            {
                _logger.Debug($"Source and target API connections are fully defined. No named connections are being used.");
                return configuration;
            }

            // Ensure that names are provided for API connections that are not already fully defined
            if (!connections.Source.IsFullyDefined() && string.IsNullOrEmpty(connections.Source.Name))
            {
                throw new ArgumentException("Source API connection is not fully defined and no connection name was provided.");
            }

            if (!connections.Target.IsFullyDefined() && string.IsNullOrEmpty(connections.Target.Name))
            {
                throw new ArgumentException("Target API connection is not fully defined and no connection name was provided.");
            }

            // If we don't have a named API connection provider, provide an appropriate failure message
            if (NamedApiConnectionDetailsReader == null)
            {
                if (!connections.Source.IsFullyDefined())
                {
                    throw new ArgumentException($"Named source API connection '{connections.Source}' could not be initialized because no named API connection provider was configured.");
                }

                if (!connections.Target.IsFullyDefined())
                {
                    throw new ArgumentException($"Named target API connection '{connections.Source}' could not be initialized because no named API connection provider was configured.");
                }
            }
            
            // Add source/target connection configuration from named connections
            return ApplyNamedConnectionConfigurations(configBuilder, connections);
        }

        private IConfiguration ApplyNamedConnectionConfigurations(
            IConfigurationBuilder configBuilder,
            Connections connections)
        {
            var additionalConfigurationValues = new List<KeyValuePair<string, string>>();
            
            // Get additional named configuration values for source, if necessary
            if (!connections.Source.IsFullyDefined())
            {
                _logger.Debug("Source connection details are not fully defined.");
                
                if (string.IsNullOrEmpty(connections.Source.Name))
                {
                    throw new ArgumentException("Source connection details were not available and no connection name was supplied.");
                }

                var sourceConfigurationValues =
                    GetNamedConnectionConfigurationValues(connections.Source.Name, ConnectionType.Source);

                additionalConfigurationValues.AddRange(sourceConfigurationValues);
            }

            // Get additional named configuration values for target, if necessary
            if (!connections.Target.IsFullyDefined())
            {
                _logger.Debug("Target connection details are not fully defined.");

                if (string.IsNullOrEmpty(connections.Target.Name))
                {
                    throw new ArgumentException("Target connection details were not configured and no connection name was supplied.");
                }

                var targetConfigurationValues =
                    GetNamedConnectionConfigurationValues(connections.Target.Name, ConnectionType.Target);

                additionalConfigurationValues.AddRange(targetConfigurationValues);
            }

            // Add in named connections (now provided as "Source" and "Target" connections)
            var configuration = configBuilder
                .AddInMemoryCollection(additionalConfigurationValues)
                .Build();
            
            // Recheck finalized connection configurations
            var finalizedConnections = configuration.Get<ConnectionConfiguration>().Connections;

            if (!finalizedConnections.Source.IsFullyDefined())
            {
                throw new ArgumentException($"Source connection '{connections.Source.Name}' was not fully configured.");
            }
            
            if (!finalizedConnections.Target.IsFullyDefined())
            {
                throw new ArgumentException($"Target connection '{connections.Target.Name}' was not fully configured.");
            }

            return configuration;
        }
        
        private IEnumerable<KeyValuePair<string, string>> GetNamedConnectionConfigurationValues(
            string apiConnectionName,
            ConnectionType connectionType)
        {
            _logger.Debug($"Obtaining {connectionType.ToString().ToLower()} API connection details for connection '{apiConnectionName}' using '{NamedApiConnectionDetailsReader.GetType().Name}'.");

            var namedApiConnectionDetails = NamedApiConnectionDetailsReader.GetNamedApiConnectionDetails(apiConnectionName);
            
            if (!namedApiConnectionDetails.IsFullyDefined())
            {
                throw new ArgumentException($"Named {connectionType.ToString().ToLower()} connection '{namedApiConnectionDetails.Name}' was not configured.");
            }

            // Fill in source configuration details
            yield return new KeyValuePair<string, string>(
                $"Connections:{connectionType.ToString()}:Url", 
                namedApiConnectionDetails.Url);

            yield return new KeyValuePair<string, string>(
                $"Connections:{connectionType.ToString()}:Key",
                namedApiConnectionDetails.Key);

            yield return new KeyValuePair<string, string>(
                $"Connections:{connectionType.ToString()}:Secret",
                namedApiConnectionDetails.Secret);

            if (!string.IsNullOrEmpty(namedApiConnectionDetails.Scope))
            {
                yield return new KeyValuePair<string, string>(
                    $"Connections:{connectionType.ToString()}:Scope",
                    namedApiConnectionDetails.Scope);
            }
            
            if (namedApiConnectionDetails.Force.HasValue)
            {
                yield return new KeyValuePair<string, string>(
                    $"Connections:{connectionType.ToString()}:Force",
                    namedApiConnectionDetails.Force.ToString());
            }

            if (namedApiConnectionDetails.LastChangeVersionProcessedByTargetName.Any())
            {
                yield return new KeyValuePair<string, string>(
                    $"Connections:{connectionType.ToString()}:LastChangeVersionsProcessed",
                    namedApiConnectionDetails.LastChangeVersionsProcessed);
            }

            if (!string.IsNullOrEmpty(namedApiConnectionDetails.Resources))
            {
                yield return new KeyValuePair<string, string>(
                    $"Connections:{connectionType.ToString()}:Resources", 
                    namedApiConnectionDetails.Resources);
            }

            // Treating Forbidden response as warning is only applicable for "target" connections
            if (namedApiConnectionDetails.TreatForbiddenPostAsWarning.HasValue
                && connectionType == ConnectionType.Target)
            {
                yield return new KeyValuePair<string, string>(
                    $"Connections:{connectionType.ToString()}:TreatForbiddenPostAsWarning", 
                    namedApiConnectionDetails.TreatForbiddenPostAsWarning.ToString());
            }
        }

        private enum ConnectionType
        {
            Source,
            Target,
        }
    }
}