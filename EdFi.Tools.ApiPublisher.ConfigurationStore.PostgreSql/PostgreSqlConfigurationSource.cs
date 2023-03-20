// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.ConfigurationStore.PostgreSql
{
    public class PostgreSqlConfigurationSource : IConfigurationSource
    {
        public string ConfigurationKeyPrefix { get; }
        public string? ConnectionString { get; }
        public string? EncryptionPassword { get; set; }

        public PostgreSqlConfigurationSource(string configurationKeyPrefix, string? connectionString, string? encryptionPassword)
        {
            // Ensure the stored-prefix includes the key separator
            ConfigurationKeyPrefix = configurationKeyPrefix.TrimEnd('/') + '/';
            ConnectionString = connectionString;
            EncryptionPassword = encryptionPassword;
        }
        
        public IConfigurationProvider Build(IConfigurationBuilder builder)
        {
            return new PostgreSqlConfigurationProvider(this);
        }
    }
}
