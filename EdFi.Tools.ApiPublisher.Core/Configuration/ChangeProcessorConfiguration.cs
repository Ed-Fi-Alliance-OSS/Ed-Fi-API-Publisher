// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.ApiClientManagement;
using Microsoft.Extensions.Configuration;
using Serilog;
using System;
using Version = EdFi.Tools.ApiPublisher.Core.Helpers.Version;

namespace EdFi.Tools.ApiPublisher.Core.Configuration
{
    public class ChangeProcessorConfiguration
    {
        // private readonly ISourceEdFiApiClientProvider _sourceEdFiApiClientProvider;
        // private readonly ITargetEdFiApiClientProvider _targetEdFiApiClientProvider;

        // private readonly Lazy<EdFiApiClient> _sourceApiClient;
        // private readonly Lazy<EdFiApiClient> _targetApiClient;

        private readonly ILogger _logger = Log.ForContext(typeof(ChangeProcessorConfiguration));

        public ChangeProcessorConfiguration(
            AuthorizationFailureHandling[] authorizationFailureHandling,
            string[] resourcesWithUpdatableKeys,
            // ApiConnectionDetails sourceApiConnectionDetails,
            // ApiConnectionDetails targetApiConnectionDetails,
            // Func<EdFiApiClient> sourceApiClientFactory,
            // Func<EdFiApiClient> targetApiClientFactory,
            Func<string>? javascriptModuleFactory,
            Options options,
            IConfigurationSection configurationStoreSection)
            // ISourceEdFiApiClientProvider sourceEdFiApiClientProvider,
            // ITargetEdFiApiClientProvider targetEdFiApiClientProvider)
        {
            // _sourceEdFiApiClientProvider = sourceEdFiApiClientProvider;
            // _targetEdFiApiClientProvider = targetEdFiApiClientProvider;
            AuthorizationFailureHandling = authorizationFailureHandling;
            ResourcesWithUpdatableKeys = resourcesWithUpdatableKeys;
            // SourceApiConnectionDetails = sourceApiConnectionDetails;
            // TargetApiConnectionDetails = targetApiConnectionDetails;
            JavascriptModuleFactory = javascriptModuleFactory;
            
            Options = options;
            ConfigurationStoreSection = configurationStoreSection;

            // _sourceApiClient = new Lazy<EdFiApiClient>(() =>
            // {
            //     // Establish connection to source API
            //     _logger.Information("Initializing source API client...");
            //
            //     return sourceApiClientFactory();
            // });
            //
            // _targetApiClient = new Lazy<EdFiApiClient>(() =>
            // {
            //     // Establish connection to target API
            //     _logger.Information("Initializing target API client...");
            //
            //     return targetApiClientFactory();
            // });
        }

        public AuthorizationFailureHandling[] AuthorizationFailureHandling { get; }
        public string[] ResourcesWithUpdatableKeys { get; }

        // public ApiConnectionDetails SourceApiConnectionDetails => _sourceEdFiApiClientProvider.GetApiClient().ConnectionDetails;
        //
        // public ApiConnectionDetails TargetApiConnectionDetails => _targetEdFiApiClientProvider.GetApiClient().ConnectionDetails;
        
        public Func<string>? JavascriptModuleFactory { get; }

        // public EdFiApiClient SourceApiClient
        // {
        //     // get => _sourceApiClient.Value;
        //     get => _sourceEdFiApiClientProvider.GetApiClient();
        // }
        //
        // public EdFiApiClient TargetApiClient
        // {
        //     get => _targetEdFiApiClientProvider.GetApiClient();
        // }
        
        public Options Options { get; }
        public IConfigurationSection ConfigurationStoreSection { get; }

        public Version SourceApiVersion { get; set; }
        public Version TargetApiVersion { get; set; }
    }
}
