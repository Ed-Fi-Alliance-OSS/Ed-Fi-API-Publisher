using EdFi.Tools.ApiPublisher.ConfigurationStore.SqlServer;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.Configuration
{
    public static class ConfigurationBuilderExtensions
    {
        public static IConfigurationBuilder AddConfigurationStoreForSqlServer(
            this IConfigurationBuilder builder,
            string configurationKeyPath,
            string? connectionString)
        {
            builder.Sources.Add(new SqlServerConfigurationSource(configurationKeyPath, connectionString));

            return builder;
        }
    }
}