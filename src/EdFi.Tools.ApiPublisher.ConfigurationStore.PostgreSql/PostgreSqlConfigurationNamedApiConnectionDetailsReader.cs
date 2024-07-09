// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.Configuration;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using Microsoft.Extensions.Configuration;
using System;

namespace EdFi.Tools.ApiPublisher.ConfigurationStore.PostgreSql
{
	public class PostgreSqlConfigurationNamedApiConnectionDetailsReader : INamedApiConnectionDetailsReader
    {
        public ApiConnectionDetails GetNamedApiConnectionDetails(
            string apiConnectionName,
            IConfigurationSection configurationStoreSection)
        {
            var postgresConfiguration = configurationStoreSection.Get<PostgresConfigurationStore>().PostgreSql;
            
            if (string.IsNullOrWhiteSpace(postgresConfiguration?.EncryptionPassword)) 
            {
                throw new Exception("The PostgreSQL Configuration Store encryption key for storing API keys and secrets was not provided.");
            }
            
            // Load named connection information from PostgreSQL configuration store
            var config = new ConfigurationBuilder()
                .AddConfigurationStoreForPostgreSql(
                    ConfigurationStoreHelper.Key(apiConnectionName),
                    postgresConfiguration.ConnectionString,
                    postgresConfiguration.EncryptionPassword)
                .Build();

            // Read the connection details from the configuration values
            var connectionDetails = config.Get<ApiConnectionDetails>();

            // Assign the connection name
            connectionDetails.Name = apiConnectionName;

            return connectionDetails;
        }
    }
}
