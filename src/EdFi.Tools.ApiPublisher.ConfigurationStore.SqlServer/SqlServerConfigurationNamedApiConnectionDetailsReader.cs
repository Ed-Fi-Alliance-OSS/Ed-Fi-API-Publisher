// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.Configuration;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.ConfigurationStore.SqlServer
{
	public class SqlServerConfigurationNamedApiConnectionDetailsReader : INamedApiConnectionDetailsReader
    {
        public ApiConnectionDetails GetNamedApiConnectionDetails(
            string apiConnectionName,
            IConfigurationSection configurationStoreSection)
        {
            var sqlServerConfiguration = configurationStoreSection.Get<SqlServerConfigurationStore>().SqlServer;

            // Load named connection information from SQL Server configuration store
            var config = new ConfigurationBuilder()
                .AddConfigurationStoreForSqlServer(ConfigurationStoreHelper.Key(apiConnectionName), sqlServerConfiguration?.ConnectionString)
                .Build();

            // Read the connection details from the configuration values
            var connectionDetails = config.Get<ApiConnectionDetails>();

            // Assign the connection name
            connectionDetails.Name = apiConnectionName;

            return connectionDetails;
        }
    }
}
