using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;

namespace EdFi.Tools.ApiPublisher.Core.Configuration
{
    public class ConfigurationBuilderFactory 
        //: IConfigurationBuilderFactory
    {
        public IConfigurationBuilder CreateConfigurationBuilder(string[] commandLineArgs)
        {
            var configBuilder = new ConfigurationBuilder()
                .AddJsonFile("apiPublisherSettings.json")
                .AddJsonFile("configurationStoreSettings.json")
                .AddEnvironmentVariables("EdFi:ApiPublisher:")
                .AddCommandLine(commandLineArgs, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    // API connections configuration
                    ["--sourceName"] = "Connections:Source:Name",
                    ["--sourceUrl"] = "Connections:Source:Url",
                    ["--sourceKey"] = "Connections:Source:Key",
                    ["--sourceSecret"] = "Connections:Source:Secret",
                    ["--sourceScope"] = "Connections:Source:Scope",
                    ["--lastChangeVersionProcessed"] = "Connections:Source:LastChangeVersionProcessed",
                    ["--targetName"] = "Connections:Target:Name",
                    ["--targetUrl"] = "Connections:Target:Url",
                    ["--targetKey"] = "Connections:Target:Key",
                    ["--targetSecret"] = "Connections:Target:Secret",
                    ["--targetScope"] = "Connections:Target:Scope",
                    
                    // Publisher Options
                    ["--bearerTokenRefreshMinutes"] = "Options:BearerTokenRefreshMinutes",
                    ["--retryStartingDelayMilliseconds"] = "Options:RetryStartingDelayMilliseconds",
                    ["--maxRetryAttempts"] = "Options:MaxRetryAttempts",
                    ["--maxDegreeOfParallelismForPostResourceItem"] = "Options:MaxDegreeOfParallelismForPostResourceItem",
                    ["--maxDegreeOfParallelismForStreamResourcePages"] = "Options:MaxDegreeOfParallelismForStreamResourcePages",
                    ["--streamingPagesWaitDurationSeconds"] = "Options:StreamingPagesWaitDurationSeconds",
                    ["--streamingPageSize"] = "Options:StreamingPageSize",
                    ["--includeDescriptors"] = "Options:IncludeDescriptors",
                    ["--errorPublishingBatchSize"] = "Options:ErrorPublishingBatchSize",
                    ["--ignoreSslErrors"] = "Options:IgnoreSslErrors",
                    ["--whatIf"] = "Options:WhatIf",
                    
                    // Resource selection (comma delimited paths - e.g. "/ed-fi/students,/ed-fi/studentSchoolAssociations")
                    ["--resources"] = "Connections:Source:Resources",
                    ["--excludeResources"] = "Connections:Source:ExcludeResources",
                    ["--skipResources"] = "Connections:Source:SkipResources",
                    ["--treatForbiddenPostAsWarning"] = "Connections:Target:TreatForbiddenPostAsWarning",
                    ["--ignoreIsolation"] = "Connections:Source:IgnoreIsolation",

                    // PostgreSQL configuration store
                    ["--configurationStoreProvider"] = "ConfigurationStore:Provider",
                    ["--postgreSqlEncryptionPassword"] = "ConfigurationStore:PostgreSql:EncryptionPassword",
                });

            return configBuilder;
        }
    }
}