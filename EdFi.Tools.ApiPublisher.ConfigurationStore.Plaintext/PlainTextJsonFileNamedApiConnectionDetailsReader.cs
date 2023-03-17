// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.Configuration;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.ConfigurationStore.Plaintext
{
    public class PlainTextJsonFileNamedApiConnectionDetailsReader : INamedApiConnectionDetailsReader
    {
        public ApiConnectionDetails GetNamedApiConnectionDetails(
            string apiConnectionName,
            IConfigurationSection configurationStoreSection)
        {
            // Build the configuration
            var config = new ConfigurationBuilder()
                .AddJsonFile("plainTextNamedConnections.json")
                .Build();
            
            var connections = config.Get<PlainTextNamedConnectionConfiguration>();
            
            return connections.Connections
                    .Where(details => details.Name != null)
                    .FirstOrDefault(details => details.Name!.Equals(apiConnectionName, StringComparison.OrdinalIgnoreCase))
                ?? new ApiConnectionDetails { Name = apiConnectionName };
        }
    }
}
