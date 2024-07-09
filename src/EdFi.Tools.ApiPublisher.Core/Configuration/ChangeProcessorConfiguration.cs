// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Configuration;
using Serilog;
using System;
using Version = EdFi.Tools.ApiPublisher.Core.Helpers.Version;

namespace EdFi.Tools.ApiPublisher.Core.Configuration
{
	public class ChangeProcessorConfiguration
    {
        private readonly ILogger _logger = Log.ForContext(typeof(ChangeProcessorConfiguration));

        public ChangeProcessorConfiguration(
            Options options,
            AuthorizationFailureHandling[] authorizationFailureHandling,
            string[] resourcesWithUpdatableKeys,
            IConfigurationSection configurationStoreSection,
            Func<string> javascriptModuleFactory)
        {
            AuthorizationFailureHandling = authorizationFailureHandling;
            ResourcesWithUpdatableKeys = resourcesWithUpdatableKeys;
            JavascriptModuleFactory = javascriptModuleFactory;
            Options = options;
            ConfigurationStoreSection = configurationStoreSection;
        }

        public AuthorizationFailureHandling[] AuthorizationFailureHandling { get; }
        public string[] ResourcesWithUpdatableKeys { get; }

        public Func<string> JavascriptModuleFactory { get; }

        public Options Options { get; }
        public IConfigurationSection ConfigurationStoreSection { get; }

        public Version SourceApiVersion { get; set; }
        public Version TargetApiVersion { get; set; }
    }
}
