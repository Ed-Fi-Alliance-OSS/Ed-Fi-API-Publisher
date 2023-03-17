// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Data;
using System.Data.SqlClient;
using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.ConfigurationStore.SqlServer
{
    public class SqlServerConfigurationChangeVersionProcessedWriter : IChangeVersionProcessedWriter
    {
        public async Task SetProcessedChangeVersionAsync(
            string sourceConnectionName,
            string targetConnectionName,
            long changeVersion,
            IConfigurationSection configurationStoreSection)
        {
            var sqlServerConfiguration = configurationStoreSection.Get<SqlServerConfigurationStore>().SqlServer;

            string lastChangeVersionProcessedKey =
                $"{ConfigurationStoreHelper.Key(sourceConnectionName)}/lastChangeVersionsProcessed";

            try
            {
                using (var conn = new SqlConnection(sqlServerConfiguration?.ConnectionString))
                {
                    await conn.OpenAsync().ConfigureAwait(false);

                    string newParameterJson;

                    using (var cmd = new SqlCommand("dbo.GetConfigurationValues", conn))
                    {
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Parameters.Add(new SqlParameter("@configurationKeyPrefix", lastChangeVersionProcessedKey));

                        using (var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow).ConfigureAwait(false))
                        {
                            var currentParameter = new JObject();
                            
                            if (await reader.ReadAsync().ConfigureAwait(false))
                            {
                                string changeVersionsJson = reader.GetString("ConfigurationValue");

                                currentParameter = JObject.Parse(string.IsNullOrEmpty(changeVersionsJson) ? "{}" : changeVersionsJson);
                            }

                            // Assign the new "LastChangeVersionProcessed" value
                            currentParameter[targetConnectionName] = changeVersion;

                            // Serialize the parameter's values
                            newParameterJson = currentParameter.ToString(Formatting.None);
                        }
                    }

                    using (var cmd = new SqlCommand("dbo.SetConfigurationValue", conn))
                    {
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Parameters.Add(new SqlParameter("@configurationKey", lastChangeVersionProcessedKey));
                        cmd.Parameters.Add(new SqlParameter("@plaintext", newParameterJson));
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception(
                    $"Failed to write updated change version of {changeVersion} for source connection '{sourceConnectionName}' to target connection '{targetConnectionName}'.", ex);
            }
        }
    }
}
