// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.ConfigurationStore.SqlServer
{
    public class SqlServerConfigurationSource : IConfigurationSource
    {
        public string ConfigurationKey { get; }
        public string ConnectionString { get; }

        public SqlServerConfigurationSource(string configurationKey, string connectionString)
        {
            // Ensure the stored-prefix includes the key separator
            ConfigurationKey = configurationKey.TrimEnd('/') + '/';
            ConnectionString = connectionString;
        }
        
        public IConfigurationProvider Build(IConfigurationBuilder builder)
        {
            return new SqlServerConfigurationProvider(this);
        }
    }
}
