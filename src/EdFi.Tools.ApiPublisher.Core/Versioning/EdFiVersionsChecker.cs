// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Linq;
using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Helpers;
using Newtonsoft.Json.Linq;
using Serilog;
using Version = EdFi.Tools.ApiPublisher.Core.Helpers.Version;

namespace EdFi.Tools.ApiPublisher.Core.Versioning;

public class EdFiVersionsChecker : IEdFiVersionsChecker
{
    private readonly ISourceEdFiApiVersionMetadataProvider _sourceEdFiApiVersionMetadataProvider;
    private readonly ITargetEdFiApiVersionMetadataProvider _targetEdFiApiVersionMetadataProvider;

    private readonly ILogger _logger = Log.ForContext(typeof(EdFiVersionsChecker));

    public EdFiVersionsChecker(
        ISourceEdFiApiVersionMetadataProvider sourceEdFiApiVersionMetadataProvider,
        ITargetEdFiApiVersionMetadataProvider targetEdFiApiVersionMetadataProvider)
    {
        _sourceEdFiApiVersionMetadataProvider = sourceEdFiApiVersionMetadataProvider;
        _targetEdFiApiVersionMetadataProvider = targetEdFiApiVersionMetadataProvider;
    }

    public async Task CheckApiVersionsAsync(ChangeProcessorConfiguration configuration)
    {
        _logger.Debug($"Loading source and target API version information...");

        var sourceVersionTask = _sourceEdFiApiVersionMetadataProvider.GetVersionMetadata();
        var targetVersionTask = _targetEdFiApiVersionMetadataProvider.GetVersionMetadata();

        await Task.WhenAll(sourceVersionTask, targetVersionTask).ConfigureAwait(false);

        var sourceVersionObject = sourceVersionTask?.Result;
        var targetVersionObject = targetVersionTask?.Result;

        if (sourceVersionObject == null)
        {
            _logger.Warning("Source connection does not support Ed-Fi version metadata. Version compatibility check for publishing operation cannot be performed.");
            return;
        }

        if (targetVersionObject == null)
        {
            _logger.Warning("Target connection does not support Ed-Fi version metadata. Version compatibility check for publishing operation cannot be performed.");
            return;
        }

        string sourceApiVersionText = sourceVersionObject.Value<string>("version");
        string targetApiVersionText = targetVersionObject.Value<string>("version");

        var sourceApiVersion = new Version(sourceApiVersionText);
        var targetApiVersion = new Version(targetApiVersionText);

        // Apply resolved API version number to the runtime configuration
        // TODO: Consider splitting this into a separate context object
        configuration.SourceApiVersion = sourceApiVersion;
        configuration.TargetApiVersion = targetApiVersion;

        // Warn if API versions don't match
        if (!sourceApiVersion.Equals(targetApiVersion))
        {
            _logger.Warning($"Source API version {sourceApiVersion} and target API version {targetApiVersion} do not match.");
        }

        // Try comparing Ed-Fi versions
        if (sourceApiVersion.IsAtLeast(3, 1) && targetApiVersion.IsAtLeast(3, 1))
        {
            var sourceEdFiVersion = GetEdFiStandardVersion(sourceVersionObject);
            var targetEdFiVersion = GetEdFiStandardVersion(targetVersionObject);

            if (sourceEdFiVersion != targetEdFiVersion)
            {
                _logger.Warning($"Source API is using Ed-Fi {sourceEdFiVersion} but target API is using Ed-Fi {targetEdFiVersion}. Some resources may not be publishable.");
            }
        }
        else
        {
            // throw new NotSupportedException("The Ed-Fi API Publisher is not compatible with Ed-Fi ODS API versions prior to v3.1.");
            _logger.Warning("Unable to verify Ed-Fi Standard versions between the source and target API since data model version information isn't available for one or both of the APIs.");
        }

        string GetEdFiStandardVersion(JObject jObject)
        {
            string edFiVersion;

            var dataModels = (JArray)jObject["dataModels"];

            edFiVersion = dataModels.Where(o => Newtonsoft.Json.Linq.Extensions.Value<string>(o["name"]) == "Ed-Fi")
                .Select(o => o["version"].Value<string>())
                .SingleOrDefault();

            return edFiVersion;
        }

        #region Sample Version Metadata

        /*
        Sample version metadata:
         
        {
            version: "3.0.0",
            informationalVersion: "3.0",
            build: "3.0.0.2088",
            apiMode: "Sandbox"
        }    

        {
            version: "3.1.0",
            informationalVersion: "3.1",
            build: "3.1.0.3450",
            apiMode: "Sandbox"
        }

        {
            version: "3.1.1",
            informationalVersion: "3.1.1",
            build: "3.1.1.3888",
            apiMode: "Sandbox",
            dataModels: [
                {
                    name: "Ed-Fi",
                    version: "3.1.0"
                },
                {
                    name: "GrandBend",
                    version: "1.0.0"
                }
            ]
        }

        {
            version: "3.2.0",
            informationalVersion: "3.2.0",
            build: "3.2.0.4982",
            apiMode: "Sandbox",
            dataModels: [
                {
                    name: "Ed-Fi",
                    version: "3.1.0"
                },
                {
                    name: "GrandBend",
                    version: "1.0.0"
                }
            ]
        }
                 
        {
            version: "3.3.0",
            informationalVersion: "3.3.0-prerelease",
            build: "1.0.0.0",
            apiMode: "Sandbox",
            dataModels: [
                {
                    name: "Ed-Fi",
                    version: "3.2.0"
                }
            ]
        }
         */
        #endregion
    }
}
