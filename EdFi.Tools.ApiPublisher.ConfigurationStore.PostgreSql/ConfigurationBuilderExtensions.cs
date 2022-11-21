using EdFi.Tools.ApiPublisher.ConfigurationStore.PostgreSql;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.Configuration
{
    public static class ConfigurationBuilderExtensions
    {
        public static IConfigurationBuilder AddConfigurationStoreForPostgreSql(
            this IConfigurationBuilder builder,
            string configurationKeyPath,
            string? connectionString,
            string? encryptionPassword)
        {
            builder.Sources.Add(new PostgreSqlConfigurationSource(configurationKeyPath, connectionString, encryptionPassword));

            return builder;
        }
    }
}