using EdFi.Tools.ApiPublisher.Configuration.SqlServer;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.Configuration
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