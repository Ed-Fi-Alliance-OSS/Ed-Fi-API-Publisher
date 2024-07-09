// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Configuration;
using System;

namespace EdFi.Tools.ApiPublisher.Core.Configuration
{
	public class AppSettingsConfigurationProvider : IAppSettingsConfigurationProvider
    {
        private readonly Lazy<IConfiguration> _configuration;
        
        public AppSettingsConfigurationProvider()
        {
            _configuration = new Lazy<IConfiguration>(() =>
            {
                string environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            
                var configuration = new ConfigurationBuilder()
                    .AddJsonFile("appSettings.json", optional: true, reloadOnChange: true)
                    .AddJsonFile($"appSettings.{environmentName}.json", optional: true)
                    .Build();

                return configuration;
            });
        }
        
        public IConfiguration GetConfiguration()
        {
            return _configuration.Value;
        }
    }
}
