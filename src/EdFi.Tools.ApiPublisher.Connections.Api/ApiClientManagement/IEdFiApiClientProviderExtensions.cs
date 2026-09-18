// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.Metadata.Versioning;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using Newtonsoft.Json.Linq;
using Serilog;

namespace EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;


public static class EdFiApiClientProviderExtensions
{

    /// <summary>
    /// Retrieves a specific Ed-Fi API URL from the version metadata if available,
    /// or returns a predefined fallback URL if the metadata is unavailable or does not contain the specified key.
    /// </summary>
    /// <param name="edFiApiClientProvider">The Ed-Fi API client provider used to fetch version metadata.</param>
    /// <param name="urlName">The name of the URL to retrieve (e.g., "dependencies", "oauth").</param>
    /// <param name="logger">
    /// Optional logger instance for capturing warnings or errors that occur during metadata retrieval or fallback resolution.
    /// </param>
    /// <returns>
    /// A <see cref="string"/> representing the absolute path of the requested URL,
    /// either retrieved from metadata or constructed from a fallback.
    /// </returns>
    /// <exception cref="InvalidConfigurationException">
    /// Thrown when the API declares this URL at an address that still carries a route placeholder, which the
    /// operator resolves by addressing the connection at a qualified URL.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the specified <paramref name="urlName"/> is not found in the metadata
    /// and no fallback is defined for it.
    /// </exception>
    public static async Task<string> GetEdFiUrlFromMetadataOrDefaultAsync(
        this IEdFiApiClientProvider edFiApiClientProvider,
        string urlName,
        ILogger logger = null)
    {
        var edFiApiClient = edFiApiClientProvider.GetApiClient();

        var versionMetadataProvider = new EdFiApiVersionMetadataProviderBase(edFiApiClient.Name, edFiApiClientProvider);

        JObject versionMetadata = null;

        try
        {
            versionMetadata = await versionMetadataProvider.GetVersionMetadata().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.Warning(ex, "Could not retrieve Ed-Fi version metadata.");
        }

        if (versionMetadata?["urls"]?[urlName]?.ToString() is string metadataUri &&
            Uri.TryCreate(metadataUri, UriKind.Absolute, out var uri))
        {
            string metadataPath = uri.AbsolutePath;

            // Refused rather than requested, because a path still carrying a placeholder is not an address.
            // Requesting it draws a 404 naming a URL with '%7B' in it, which reads as a defect in the tool
            // rather than as an API that was asked for its URLs at the wrong address.
            if (EdFiApiUrlSegmentResolver.ContainsRoutePlaceholder(metadataPath))
            {
                throw new InvalidConfigurationException(
                    $"The {urlName} URL declared by the {edFiApiClient.Name} API is '{metadataUri}', which still carries a route placeholder. {EdFiApiUrlSegmentResolver.RouteQualifierGuidance}");
            }

            return metadataPath;
        }

        logger?.Warning("No valid {UrlName:l} URL found in metadata. Using default fallback.", urlName);
        switch (urlName)
        {
            case "dependencies":
                return $"metadata/{edFiApiClient.DataManagementApiSegment}/dependencies";

            case "oauth":
                return $"oauth/token";

            default:
                string message = $"No fallback is defined for urlName '{urlName}'. Cannot resolve URI.";
                logger?.Error(message);
                throw new InvalidOperationException(message);
        }
    }
}
