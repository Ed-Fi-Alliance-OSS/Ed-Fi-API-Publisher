// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.Data;
using Npgsql;

namespace EdFi.Tools.ApiPublisher.ConfigurationStore.PostgreSql
{
    public class PostgreSqlConfigurationValuesProvider
    {
        public IDictionary<string, string> GetConfigurationValues(
            string connectionString, 
            string encryptionPassword, 
            string configurationKeyPrefix)
        {
            using var conn = new NpgsqlConnection(connectionString);

            conn.Open();

            string sql = @"
SELECT  configuration_key, pgp_sym_decrypt(configuration_value_encrypted, @encryptionPassword) as configuration_value
FROM    dbo.configuration_value
WHERE   configuration_key LIKE @configurationKeyPrefix
        AND configuration_value_encrypted IS NOT NULL
UNION
SELECT  configuration_key, configuration_value
FROM    dbo.configuration_value
WHERE   configuration_key LIKE @configurationKeyPrefix
        AND configuration_value.configuration_value IS NOT NULL;
";
            using var cmd = new NpgsqlCommand(sql, conn);

            // Apply encryption password
            cmd.Parameters.Add(new NpgsqlParameter("@encryptionPassword", encryptionPassword));

            // Apply configuration key prefix parameter
            cmd.Parameters.Add(
                new NpgsqlParameter(
                    "@configurationKeyPrefix",
                    string.IsNullOrEmpty(configurationKeyPrefix)
                        ? "%"
                        : $"{configurationKeyPrefix}%"));

            using var reader = cmd.ExecuteReader(CommandBehavior.CloseConnection);

            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            while (reader.Read())
            {
                string key = reader.GetString("configuration_key");
                string value = reader.GetString("configuration_value");

                // Trim the "prefix" off the value returned
                if (!string.IsNullOrEmpty(configurationKeyPrefix))
                {
                    key = key.Substring(configurationKeyPrefix.Length);
                }

                settings.Add(key, value);
            }

            return settings;
        }
    }
}
