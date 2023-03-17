// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
