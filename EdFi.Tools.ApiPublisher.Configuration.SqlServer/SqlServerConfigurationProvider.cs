using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Configuration.SqlServer
{
    public class SqlServerConfigurationProvider : ConfigurationProvider
    {
        private readonly SqlServerConfigurationSource _sqlServerConfigurationSource;

        public SqlServerConfigurationProvider(SqlServerConfigurationSource sqlServerConfigurationSource)
        {
            _sqlServerConfigurationSource = sqlServerConfigurationSource;
        }

        public override void Load()
        {
            using var conn = new SqlConnection(_sqlServerConfigurationSource.ConnectionString);

            conn.Open();

            using var cmd = new SqlCommand("dbo.GetConfigurationValues", conn);

            cmd.CommandType = CommandType.StoredProcedure;

            // Apply prefix parameter if supplied
            if (!string.IsNullOrEmpty(_sqlServerConfigurationSource.ConfigurationKey))
            {
                cmd.Parameters.Add(new SqlParameter("@configurationKeyPrefix",
                    _sqlServerConfigurationSource.ConfigurationKey));
            }

            using var reader = cmd.ExecuteReader(CommandBehavior.CloseConnection);

            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            while (reader.Read())
            {
                string key = reader.GetString("ConfigurationKey");
                string value = reader.GetString("ConfigurationValue");

                // Trim the "prefix" off the value returned
                if (!string.IsNullOrEmpty(_sqlServerConfigurationSource.ConfigurationKey))
                {
                    key = key.Substring(_sqlServerConfigurationSource.ConfigurationKey.Length);
                }
                            
                settings.Add(key, value);
            }

            Data = settings;
        }
    }
}