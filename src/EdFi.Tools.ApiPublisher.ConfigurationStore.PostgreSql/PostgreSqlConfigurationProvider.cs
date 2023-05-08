// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.ConfigurationStore.PostgreSql
{
    public class PostgreSqlConfigurationProvider : ConfigurationProvider
    {
        private readonly PostgreSqlConfigurationSource _postgreSqlConfigurationSource;

        public PostgreSqlConfigurationProvider(PostgreSqlConfigurationSource postgreSqlConfigurationSource)
        {
            _postgreSqlConfigurationSource = postgreSqlConfigurationSource;
        }

        public override void Load()
        {
            var settings = new PostgreSqlConfigurationValuesProvider()
                .GetConfigurationValues(
                    _postgreSqlConfigurationSource.ConnectionString, 
                    _postgreSqlConfigurationSource.EncryptionPassword, 
                    _postgreSqlConfigurationSource.ConfigurationKeyPrefix);

            Data = settings;
        }
    }
}
