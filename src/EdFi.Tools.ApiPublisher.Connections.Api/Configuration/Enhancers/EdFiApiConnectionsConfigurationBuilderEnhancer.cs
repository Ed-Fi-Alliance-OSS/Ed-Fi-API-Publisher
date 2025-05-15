// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Configuration.Enhancers;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Configuration.Enhancers
{
    public class EdFiApiConnectionsConfigurationBuilderEnhancer : IConfigurationBuilderEnhancer
    {
        private readonly ILogger _logger = Log.Logger.ForContext(typeof(EdFiApiConnectionsConfigurationBuilderEnhancer));
        private readonly INamedApiConnectionDetailsReader _namedApiConnectionDetailsReader;

        public EdFiApiConnectionsConfigurationBuilderEnhancer(INamedApiConnectionDetailsReader namedApiConnectionDetailsReader)
        {
            _namedApiConnectionDetailsReader = namedApiConnectionDetailsReader;
        }

        public void Enhance(IConfigurationRoot initialConfiguration, IConfigurationBuilder configurationBuilder)
        {
            var connectionsConfiguration = initialConfiguration.GetSection("Connections");

            var sourceConnectionConfiguration = connectionsConfiguration.GetSection("Source");
            var sourceConnectionDetails = sourceConnectionConfiguration.Get<ApiConnectionDetails>();

            var targetConnectionConfiguration = connectionsConfiguration.GetSection("Target");
            var targetConnectionDetails = targetConnectionConfiguration.Get<ApiConnectionDetails>();

            // Get the Configuration Store section
            var configurationStoreSection = initialConfiguration.GetSection("configurationStore");

            // Add source/target connection configuration from named connections
            var additionalConfigurationValues = new List<KeyValuePair<string, string>>();

            // Get additional named configuration values for source, as necessary
            if (!sourceConnectionDetails.IsFullyDefined())
            {
                additionalConfigurationValues.AddRange(
                    GetEnhancedConnectionConfigurationValues(sourceConnectionDetails, ConnectionRole.Source));
            }

            // Get additional named configuration values for target, as necessary
            if (!targetConnectionDetails.IsFullyDefined())
            {
                additionalConfigurationValues.AddRange(
                    GetEnhancedConnectionConfigurationValues(targetConnectionDetails, ConnectionRole.Target));
            }

            // Add in named connections (now provided as "Source" and "Target" connections)
            var enhancedConfiguration = configurationBuilder.AddInMemoryCollection(additionalConfigurationValues).Build();

            // Recheck finalized connection configurations
            var finalizedConnections = enhancedConfiguration.Get<ConnectionConfiguration>().Connections;

            if (!finalizedConnections.Source.IsFullyDefined())
            {
                throw new ArgumentException($"Source connection '{sourceConnectionDetails.Name}' was not fully configured.");
            }

            if (!finalizedConnections.Target.IsFullyDefined())
            {
                throw new ArgumentException($"Target connection '{targetConnectionDetails.Name}' was not fully configured.");
            }

            IEnumerable<KeyValuePair<string, string>> GetEnhancedConnectionConfigurationValues(
                ApiConnectionDetails connection,
                ConnectionRole connectionType)
            {
                _logger.Debug("{ConnectionType} connection details are not fully defined.", connectionType);

                if (string.IsNullOrEmpty(connection.Name))
                {
                    throw new ArgumentException(
                            $"{connectionType} connection details were not available and no connection name was supplied.");
                }

                var configurationValues = CreateNamedConnectionConfigurationValues(connection.Name, connectionType).ToArray();

                return configurationValues;
            }

            IEnumerable<KeyValuePair<string, string>> CreateNamedConnectionConfigurationValues(
                string apiConnectionName,
                ConnectionRole connectionRole)
            {
                _logger.Debug("Obtaining {ConnectionRole} API connection details for connection '{ApiConnectionName}' using '{NamedApiConnectionDetailsReaderName}'.",
                    connectionRole.ToString().ToLower(), apiConnectionName, _namedApiConnectionDetailsReader.GetType().Name);

                var namedApiConnectionDetails =
                    _namedApiConnectionDetailsReader.GetNamedApiConnectionDetails(apiConnectionName, configurationStoreSection);

                if (!namedApiConnectionDetails.IsFullyDefined())
                {
                    throw new ArgumentException(
                        $"Named {connectionRole.ToString().ToLower()} connection '{namedApiConnectionDetails.Name}' was not fully configured. The following values are missing: [{string.Join(", ", namedApiConnectionDetails.MissingConfigurationValues())}]");
                }

                // Fill in source configuration details
                yield return new KeyValuePair<string, string>(
                    $"Connections:{connectionRole}:Url",
                    namedApiConnectionDetails.Url);

                yield return new KeyValuePair<string, string>(
                    $"Connections:{connectionRole}:AuthUrl",
                    namedApiConnectionDetails.AuthUrl);

                yield return new KeyValuePair<string, string>(
                    $"Connections:{connectionRole}:Key",
                    namedApiConnectionDetails.Key);

                yield return new KeyValuePair<string, string>(
                    $"Connections:{connectionRole}:Secret",
                    namedApiConnectionDetails.Secret);

                if (!string.IsNullOrEmpty(namedApiConnectionDetails.Scope))
                {
                    yield return new KeyValuePair<string, string>(
                        $"Connections:{connectionRole}:Scope",
                        namedApiConnectionDetails.Scope);
                }

                if (namedApiConnectionDetails.IgnoreIsolation.HasValue)
                {
                    yield return new KeyValuePair<string, string>(
                        $"Connections:{connectionRole}:Force",
                        namedApiConnectionDetails.IgnoreIsolation.ToString());
                }

                if (namedApiConnectionDetails.LastChangeVersionProcessedByTargetName.Any())
                {
                    yield return new KeyValuePair<string, string>(
                        $"Connections:{connectionRole}:LastChangeVersionsProcessed",
                        namedApiConnectionDetails.LastChangeVersionsProcessed);
                }

                if (!string.IsNullOrEmpty(namedApiConnectionDetails.Include))
                {
                    yield return new KeyValuePair<string, string>(
                        $"Connections:{connectionRole}:Include",
                        namedApiConnectionDetails.Include);
                }

                if (!string.IsNullOrEmpty(namedApiConnectionDetails.IncludeOnly))
                {
                    yield return new KeyValuePair<string, string>(
                        $"Connections:{connectionRole}:IncludeOnly",
                        namedApiConnectionDetails.IncludeOnly);
                }

                if (!string.IsNullOrEmpty(namedApiConnectionDetails.Exclude))
                {
                    yield return new KeyValuePair<string, string>(
                        $"Connections:{connectionRole}:Exclude",
                        namedApiConnectionDetails.Exclude);
                }

                if (!string.IsNullOrEmpty(namedApiConnectionDetails.ExcludeOnly))
                {
                    yield return new KeyValuePair<string, string>(
                        $"Connections:{connectionRole}:ExcludeOnly",
                        namedApiConnectionDetails.ExcludeOnly);
                }

                // Treating Forbidden response as warning is only applicable for "target" connections
                if (namedApiConnectionDetails.TreatForbiddenPostAsWarning.HasValue && connectionRole == ConnectionRole.Target)
                {
                    yield return new KeyValuePair<string, string>(
                        $"Connections:{connectionRole}:TreatForbiddenPostAsWarning",
                        namedApiConnectionDetails.TreatForbiddenPostAsWarning.ToString());
                }
            }
        }

        private enum ConnectionRole
        {
            Source,
            Target,
        }
    }
}
