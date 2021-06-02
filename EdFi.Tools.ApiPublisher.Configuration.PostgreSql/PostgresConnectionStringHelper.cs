using System;
using System.IO;
using System.Linq;
using Npgsql;

namespace EdFi.Tools.ApiPublisher.Configuration.PostgreSql
{
    public static class PostgresConnectionStringHelper
    {
        public static string ProcessPassfile(string connectionString)
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);

            if (builder.Username != null || builder.Password != null)
            {
                return connectionString;
            }
            
            // Try to get the PASSFILE location from connection string
            string pgPassFile = builder.Passfile;

            // If not found in connection string...
            if (string.IsNullOrEmpty(pgPassFile))
            {
                // Look for the environment variable
                pgPassFile = Environment.GetEnvironmentVariable("PGPASSFILE");
            }

            // else TODO: Could re-implement documented fallback locations here
            
            // If we have a path for a PASSFILE, proceed with processing it...
            if (!string.IsNullOrEmpty(pgPassFile))
            {
                string pgPassFileText = File.ReadAllText(pgPassFile);
                var pgPassFileEntries = pgPassFileText.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

                var matchingCredentials = pgPassFileEntries.Select(e => e.Split(":"))
                    .Where(parts => parts[0].Equals(builder.Host, StringComparison.OrdinalIgnoreCase) || parts[0] == "*")
                    .Where(parts => parts[1].Equals(builder.Port.ToString()) || parts[1] == "*")
                    .Where(parts => parts[2].Equals(builder.Database, StringComparison.OrdinalIgnoreCase) || parts[2] == "*")
                    .Select(
                        parts => new
                        {
                            Username = parts[3],
                            Password = parts[4]
                        })
                    .FirstOrDefault();

                //
                builder.Username = matchingCredentials?.Username;
                builder.Password = matchingCredentials?.Password;

                return builder.ConnectionString;
            }

            return connectionString;
        }
    }
}