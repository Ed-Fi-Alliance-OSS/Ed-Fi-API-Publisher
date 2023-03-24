// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.ConfigurationStore.PostgreSql;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.Configuration
{
    public static class ConfigurationBuilderExtensions
    {
        public static IConfigurationBuilder AddConfigurationStoreForPostgreSql(
            this IConfigurationBuilder builder,
            string configurationKeyPath,
            string connectionString,
            string encryptionPassword)
        {
            builder.Sources.Add(new PostgreSqlConfigurationSource(configurationKeyPath, connectionString, encryptionPassword));

            return builder;
        }
    }
}
