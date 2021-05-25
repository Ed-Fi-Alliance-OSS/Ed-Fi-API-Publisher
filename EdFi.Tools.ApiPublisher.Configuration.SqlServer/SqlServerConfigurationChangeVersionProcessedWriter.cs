using System;
using System.Data;
using System.Data.SqlClient;
using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing;
using log4net;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Configuration.SqlServer
{
    public class SqlServerConfigurationChangeVersionProcessedWriter : IChangeVersionProcessedWriter
    {
        private readonly ILog _logger =
            LogManager.GetLogger(typeof(SqlServerConfigurationChangeVersionProcessedWriter));

        private readonly Lazy<string> _connectionString;

        public SqlServerConfigurationChangeVersionProcessedWriter(IAppSettingsConfigurationProvider appSettingsConfigurationProvider)
        {
            _connectionString = new Lazy<string>(
                () =>
                {
                    var configuration = appSettingsConfigurationProvider.GetConfiguration();
                    return configuration.GetConnectionString("SqlServerConfiguration");
                });
        }

        public async Task SetProcessedChangeVersionAsync(
            string sourceConnectionName,
            string targetConnectionName,
            long changeVersion)
        {
            string lastChangeVersionProcessedKey =
                $"/ed-fi/publisher/connections/{sourceConnectionName}/lastChangeVersionsProcessed";

            try
            {
                using (var conn = new SqlConnection(_connectionString.Value))
                {
                    await conn.OpenAsync();

                    string newParameterJson;

                    using (var cmd = new SqlCommand("dbo.GetConfigurationValues", conn))
                    {
                        cmd.CommandType = CommandType.StoredProcedure;
                        cmd.Parameters.Add(new SqlParameter("@configurationKeyPrefix", lastChangeVersionProcessedKey));

                        using (var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow))
                        {
                            var currentParameter = new JObject();
                            
                            if (await reader.ReadAsync())
                            {
#if NETCOREAPP
                                string changeVersionsJson = reader.GetString(reader.GetOrdinal("ConfigurationValue"));
#elif NETCOREAPP3_1
                                string changeVersionsJson = reader.GetString("ConfigurationValue");
#endif
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