// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;
using System;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.ConfigurationStore.PostgreSql
{
    public class PostgreSqlConfigurationChangeVersionProcessedWriter : IChangeVersionProcessedWriter
    {
        public async Task SetProcessedChangeVersionAsync(
            string sourceConnectionName,
            string targetConnectionName,
            long changeVersion,
            IConfigurationSection configurationStoreSection)
        {
            var postgresConfiguration = configurationStoreSection.Get<PostgresConfigurationStore>().PostgreSql;

            // Make sure Postgres configuration has encryption key provided
            if (string.IsNullOrWhiteSpace(postgresConfiguration?.EncryptionPassword))
            {
                throw new Exception("The PostgreSQL Configuration Store encryption key for storing API keys and secrets was not provided.");
            }

            try
            {
                var configurationValues = new PostgreSqlConfigurationValuesProvider()
                    .GetConfigurationValues(
                        postgresConfiguration.ConnectionString,
                        postgresConfiguration.EncryptionPassword,
                        ConfigurationStoreHelper.Key(sourceConnectionName));

                var currentParameter = new JObject();

                if (configurationValues.TryGetValue("lastChangeVersionsProcessed", out string changeVersionsJson))
                {
                    currentParameter = JObject.Parse(string.IsNullOrEmpty(changeVersionsJson) ? "{}" : changeVersionsJson);
                }

                // Assign the new "LastChangeVersionProcessed" value
                currentParameter[targetConnectionName] = changeVersion;

                // Serialize the parameter's values
                var newParameterJson = currentParameter.ToString(Formatting.None);

                string upsertSql = @"
INSERT INTO dbo.configuration_value (configuration_key, configuration_value)
VALUES (@configurationKey, @configurationValue)
ON CONFLICT (configuration_key)
DO UPDATE SET configuration_value = @configurationValue;
";
                await using var conn = new NpgsqlConnection(postgresConfiguration.ConnectionString);
                await conn.OpenAsync().ConfigureAwait(false);
                await using var cmd = new NpgsqlCommand(upsertSql, conn);

                string lastChangeVersionProcessedKey =
                    $"{ConfigurationStoreHelper.Key(sourceConnectionName)}/lastChangeVersionsProcessed";

                cmd.Parameters.Add(new NpgsqlParameter("@configurationKey", lastChangeVersionProcessedKey));
                cmd.Parameters.Add(new NpgsqlParameter("@configurationValue", newParameterJson));
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new Exception(
                    $"Failed to write updated change version of {changeVersion} for source connection '{sourceConnectionName}' to target connection '{targetConnectionName}'.", ex);
            }
        }
    }
}
