// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Core.Configuration
{
    public class ConfigurationBuilderFactory
    {
        /// <summary>
        /// Creates a configuration builder incorporating settings files, environment variables and command-line arguments.
        /// </summary>
        /// <param name="commandLineArgs"></param>
        /// <returns></returns>
        public IConfigurationBuilder Create(string[] commandLineArgs)
        {
            var configBuilder = new ConfigurationBuilder()
                .AddJsonFile("apiPublisherSettings.json")
                .AddJsonFile("configurationStoreSettings.json")
                .AddEnvironmentVariables("EdFi:ApiPublisher:")
                .AddCommandLine(commandLineArgs, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    // Source connection configuration
                    ["--sourceName"] = "Connections:Source:Name",
                    ["--sourceType"] = "Connections:Source:Type", // api | sqlite

                    // Source API connection configuration
                    ["--sourceUrl"] = "Connections:Source:Url",
                    ["--sourceAuthUrl"] = "Connections:Source:AuthUrl",
                    ["--sourceKey"] = "Connections:Source:Key",
                    ["--sourceSecret"] = "Connections:Source:Secret",
                    ["--sourceScope"] = "Connections:Source:Scope",
                    ["--sourceSchoolYear"] = "Connections:Source:SchoolYear",
                    ["--lastChangeVersionProcessed"] = "Connections:Source:LastChangeVersionProcessed",

                    // Temporary argument -- until Ed-Fi ODS API corrects issues with Profiles enforcement
                    ["--sourceProfileName"] = "Connections:Source:ProfileName",

                    // Target connection configuration
                    ["--targetName"] = "Connections:Target:Name",
                    ["--targetType"] = "Connections:Target:Type", // api | sqlite

                    // Target API connection configuration
                    ["--targetUrl"] = "Connections:Target:Url",
                    ["--targetAuthUrl"] = "Connections:Target:AuthUrl",
                    ["--targetKey"] = "Connections:Target:Key",
                    ["--targetSecret"] = "Connections:Target:Secret",
                    ["--targetScope"] = "Connections:Target:Scope",
                    ["--targetSchoolYear"] = "Connections:Target:SchoolYear",

                    // Temporary argument -- until Ed-Fi ODS API corrects issues with Profiles enforcement
                    ["--targetProfileName"] = "Connections:Target:ProfileName",

                    // Target SqlLite connection configuration
                    ["--targetUrl"] = "Connections:Target:Url",

                    // Publisher Options
                    ["--bearerTokenRefreshMinutes"] = "Options:BearerTokenRefreshMinutes",
                    ["--retryStartingDelayMilliseconds"] = "Options:RetryStartingDelayMilliseconds",
                    ["--maxRetryAttempts"] = "Options:MaxRetryAttempts",
                    ["--maxDegreeOfParallelismForResourceProcessing"] = "Options:MaxDegreeOfParallelismForResourceProcessing",
                    ["--maxDegreeOfParallelismForPostResourceItem"] = "Options:MaxDegreeOfParallelismForPostResourceItem",
                    ["--maxDegreeOfParallelismForStreamResourcePages"] = "Options:MaxDegreeOfParallelismForStreamResourcePages",
                    ["--streamingPagesWaitDurationSeconds"] = "Options:StreamingPagesWaitDurationSeconds",
                    ["--streamingPageSize"] = "Options:StreamingPageSize",
                    ["--includeDescriptors"] = "Options:IncludeDescriptors",
                    ["--errorPublishingBatchSize"] = "Options:ErrorPublishingBatchSize",
                    ["--ignoreSslErrors"] = "Options:IgnoreSslErrors",
                    ["--useSourceDependencyMetadata"] = "Options:UseSourceDependencyMetadata",
                    ["--whatIf"] = "Options:WhatIf",
                    ["--useChangeVersionPaging"] = "Options:UseChangeVersionPaging",
                    ["--changeVersionPagingWindowSize"] = "Options:ChangeVersionPagingWindowSize",
                    ["--enableRateLimit"] = "Options:EnableRateLimit",
                    ["--rateLimitNumberExecutions"] = "Options:RateLimitNumberExecutions",
                    ["--rateLimitTimeSeconds"] = "Options:RateLimitTimeSeconds",
                    ["--rateLimitMaxRetries"] = "Options:RateLimitMaxRetries",
                    ["--useReversePaging"] = "Options:UseReversePaging",
                    ["--lastChangeVersionProcessedNamespace"] = "Options:LastChangeVersionProcessedNamespace",


                    // Resource selection (comma delimited paths - e.g. "/ed-fi/students,/ed-fi/studentSchoolAssociations")
                    ["--include"] = "Connections:Source:Include",
                    ["--includeOnly"] = "Connections:Source:IncludeOnly",
                    ["--exclude"] = "Connections:Source:Exclude",
                    ["--excludeOnly"] = "Connections:Source:ExcludeOnly",

                    // Obsolete command-line arguments (setters throw exceptions for now)
                    ["--resources"] = "Connections:Source:Resources",
                    ["--excludeResources"] = "Connections:Source:ExcludeResources",
                    ["--skipResources"] = "Connections:Source:SkipResources",

                    ["--treatForbiddenPostAsWarning"] = "Connections:Target:TreatForbiddenPostAsWarning",
                    ["--ignoreIsolation"] = "Connections:Source:IgnoreIsolation",

                    // PostgreSQL configuration store
                    ["--configurationStoreProvider"] = "ConfigurationStore:Provider",
                    ["--postgreSqlEncryptionPassword"] = "ConfigurationStore:PostgreSql:EncryptionPassword",

                    // Path to the folder containing for JavaScript extension for special handling and retries
                    ["--remediationsScriptFile"] = "Options:RemediationsScriptFile",
                });

            return configBuilder;
        }
    }
}
