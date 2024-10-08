// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Dependencies;
using Serilog;
using System.Net.Http.Headers;
using System.Xml.Linq;

namespace EdFi.Tools.ApiPublisher.Connections.Api.Metadata.Dependencies;

/// <summary>
/// Provides Ed-Fi ODS API metadata from an Ed-Fi ODS API endpoint.
/// </summary>
public class EdFiApiGraphMLDependencyMetadataProvider : IGraphMLDependencyMetadataProvider
{
    private readonly IEdFiApiClientProvider _edFiApiClientProvider;

    private readonly ILogger _logger = Log.ForContext(typeof(EdFiApiGraphMLDependencyMetadataProvider));

    public EdFiApiGraphMLDependencyMetadataProvider(IEdFiApiClientProvider edFiApiClientProvider)
    {
        _edFiApiClientProvider = edFiApiClientProvider;
    }

    public async Task<(XElement, XNamespace)> GetDependencyMetadataAsync()
    {
        var edFiApiClient = _edFiApiClientProvider.GetApiClient();

        string dependenciesRequestUri = $"metadata/{edFiApiClient.DataManagementApiSegment}/dependencies";

        // Get the resource dependencies from the target
        _logger.Information("Getting dependencies from API at {BaseAddress}{DependenciesRequestUri}...",
            edFiApiClient.HttpClient.BaseAddress, dependenciesRequestUri);

        var dependencyRequest = new HttpRequestMessage(HttpMethod.Get, dependenciesRequestUri);
        dependencyRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/graphml"));
        var dependencyResponse = await edFiApiClient.HttpClient.SendAsync(dependencyRequest).ConfigureAwait(false);

        string dependencyResponseContent = await dependencyResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!dependencyResponse.IsSuccessStatusCode)
        {
            var message = $"Ed-Fi ODS API request for dependencies to '{dependencyRequest.RequestUri}' returned '{dependencyResponse.StatusCode}' with content:{Environment.NewLine}{dependencyResponseContent}";
            _logger.Error(message);
            throw new Exception("Resource dependencies could not be obtained.");
        }

        XNamespace ns = "http://graphml.graphdrawing.org/xmlns";

        try
        {
            var dependencyGraphML = XElement.Parse(dependencyResponseContent);
            return (dependencyGraphML, ns);
        }
        catch (Exception ex)
        {
            var message = $"Unable to parse dependency response as GraphML: {dependencyResponseContent}{Environment.NewLine}{ex}";
            _logger.Error(ex, message);
            throw new Exception("Resource dependencies could not be obtained.");
        }
    }
}
