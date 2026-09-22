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
    /// <param name="urlName">The name of the URL to retrieve (e.g., "dependencies").</param>
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
            // The same rule the path segments follow, and for the same reason: this value comes from the
            // remote document, and the request built from it is sent on the authenticated client, so a value
            // naming another host would carry this connection's bearer token there.
            var connectionAddress = edFiApiClient.HttpClient.BaseAddress;

            if (
                connectionAddress is not null
                && !string.Equals(uri.Host, connectionAddress.Host, StringComparison.OrdinalIgnoreCase)
            )
            {
                throw new InvalidConfigurationException(
                    $"The {urlName} URL declared by the {edFiApiClient.Name} API is '{EdFiApiUrlSegmentResolver.ForLog(metadataUri)}', which is not served by the host this connection addresses ('{connectionAddress}'). A connection's requests carry its credentials, so they are only ever sent to its own host.");
            }

            // Kept absolute, unlike the path segments, which are held relative to the connection URL. The
            // difference is deliberate: this value is used as a request URI of its own, and a leading slash
            // resolves against the authority root, where the declared path already carries whatever prefix the
            // connection URL has. A segment is concatenated onto the connection URL instead, so an absolute
            // path there would state that prefix twice.
            string metadataPath = uri.AbsolutePath;

            // A path opening with two slashes is a network-path reference, not a path: an HttpClient reads
            // '//evil.test/deps' as a host and leaves the connection's address entirely, which is how a
            // declared URL of 'https://api//evil.test/deps' would send the bearer token elsewhere. The host
            // check above already refuses the declaration, so this is the second line rather than the first.
            if (metadataPath.StartsWith("//", StringComparison.Ordinal))
            {
                throw new InvalidConfigurationException(
                    $"The {urlName} URL declared by the {edFiApiClient.Name} API is '{EdFiApiUrlSegmentResolver.ForLog(metadataUri)}', whose path opens with '//'. That is read as another host rather than as a path on this one.");
            }

            // Refused rather than requested, because a path still carrying a placeholder is not an address.
            // Requesting it draws a 404 naming a URL with '%7B' in it, which reads as a defect in the tool
            // rather than as an API that was asked for its URLs at the wrong address.
            if (EdFiApiUrlSegmentResolver.ContainsRoutePlaceholder(metadataPath))
            {
                throw new InvalidConfigurationException(
                    $"The {urlName} URL declared by the {edFiApiClient.Name} API is '{EdFiApiUrlSegmentResolver.ForLog(metadataUri)}', which still carries a route placeholder. {EdFiApiUrlSegmentResolver.RouteQualifierGuidance}");
            }

            return metadataPath;
        }

        logger?.Warning("No valid {UrlName:l} URL found in metadata. Using default fallback.", urlName);
        switch (urlName)
        {
            case "dependencies":
                // The conventional location puts the data management path inside 'metadata/.../dependencies',
                // which only composes while that path stays beneath the connection's own address. A segment
                // that climbs out of it, which an API declaring its data management outside the connection's
                // prefix produces, cancels the 'metadata' element instead of sitting inside it: a connection
                // at '/edfi/' with a segment of '../other/data' composes
                // 'https://server/edfi/other/data/dependencies', which names nothing. There is no telling
                // where such an API keeps its metadata, so this is refused rather than guessed at.
                if (EdFiApiUrlSegmentResolver.ClimbsAboveConnection(edFiApiClient.DataManagementApiSegment))
                {
                    throw new InvalidConfigurationException(
                        $"The {edFiApiClient.Name} API declares no {urlName} URL, and its data management path '{EdFiApiUrlSegmentResolver.ForLog(edFiApiClient.DataManagementApiSegment)}' is served above the address this connection uses, so the conventional location for {urlName} cannot be composed from it. Address the connection at the URL the API serves from, or have the API declare its {urlName} URL.");
                }

                return $"metadata/{edFiApiClient.DataManagementApiSegment}/dependencies";

            default:
                string message = $"No fallback is defined for urlName '{urlName}'. Cannot resolve URI.";
                logger?.Error(message);
                throw new InvalidOperationException(message);
        }
    }

}
