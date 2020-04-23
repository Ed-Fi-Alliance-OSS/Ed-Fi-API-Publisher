using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Configuration.SqlServer
{
    public static class ConfigurationBuilderExtensions
    {
        public static IConfigurationBuilder AddSqlServerConfiguration(
            this IConfigurationBuilder builder,
            string configurationKeyPath,
            string connectionString)
        {
            builder.Sources.Add(new SqlServerConfigurationSource(configurationKeyPath, connectionString));

            return builder;
        }
    }
}