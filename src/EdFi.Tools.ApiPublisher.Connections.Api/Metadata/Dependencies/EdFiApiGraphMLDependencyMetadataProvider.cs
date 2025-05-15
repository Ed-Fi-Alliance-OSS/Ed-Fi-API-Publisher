// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using EdFi.Tools.ApiPublisher.Connections.Api.ApiClientManagement;
using EdFi.Tools.ApiPublisher.Core.Dependencies;
using Newtonsoft.Json;
using QuickGraph;
using Serilog;

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
        // try get dependencies Uri from metadata if not default value
        string dependenciesRequestUri = await _edFiApiClientProvider.GetEdFiUrlFromMetadataOrDefaultAsync("dependencies", _logger);

        var edFiApiClient = _edFiApiClientProvider.GetApiClient();

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

        if (IsJson(dependencyResponseContent, dependencyResponse))
        {
            _logger.Information("Detected a dependencies response in JSON format (resource dependency list). Attempting to convert it to GraphML (XML).");
            try
            {
                dependencyResponseContent = CreateResourceGraphFromJson(dependencyResponseContent);

            }
            catch (Exception ex)
            {
                var message = $"Detected dependencies response is JSON. Unable to parse dependency response as JSON: {dependencyResponseContent}{Environment.NewLine}{ex}";
                _logger.Error(ex, message);
                throw new Exception("Resource dependencies could not be obtained.");
            }
        }

        try
        {
            XNamespace ns = "http://graphml.graphdrawing.org/xmlns";
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

    private static bool IsJson(string content, HttpResponseMessage response)
    {
        return response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true
               && (content.TrimStart().StartsWith('{')
               || content.TrimStart().StartsWith('['));
    }

    /// <summary>
    /// Converts a JSON-formatted dependency list representing Ed-Fi resources and their relationships
    /// into a GraphML XML string. The JSON input is expected to be a flat or structured list of resource items,
    /// each with a resource identifier and an order value indicating dependency level.
    /// The method generates GraphML nodes for each resource and creates directed edges from resources with
    /// lower order values to those with higher order values to represent dependencies.
    /// </summary>
    /// <param name="json">Dependency List (JSON) Ed-Fi flat or structured list showing resources and their relations</param>
    /// <returns></returns>
    public static string CreateResourceGraphFromJson(string json)
    {

        var allDependencies = JsonConvert.DeserializeObject<List<ResourceItem>>(json);

        // exclude schoolYearTypes
        var dependencies = allDependencies
                        .Where(i => !i.Resource.EndsWith("schoolYearTypes"));

        var bidirectionalGraph = new BidirectionalGraph<string, Edge<string>>();

        // Collect all unique resource names
        var resourceNames = dependencies
            .SelectMany(d => new[] { d.Resource })
            .Distinct();

        // Add vertices (resources)
        bidirectionalGraph.AddVertexRange(resourceNames);

        // Add edges
        // var edges = dependencies.Select(d => new Edge<string>(d.source, d.target))

        // Add edges (simplified: lower order → higher order)
        var grouped = dependencies.GroupBy(x => x.Order)
                           .OrderBy(g => g.Key)
                           .Select(g => g.OrderBy(x => x.Resource).ToList())
                           .ToList();

        var edges = new List<Edge<string>>();
        for (int i = 0; i < grouped.Count - 1; i++)
        {
            foreach (var source in grouped[i])
            {
                for (int j = i + 1; j < grouped.Count; j++)
                {
                    foreach (var target in grouped[j])
                    {
                        edges.Add(new Edge<string>(source.Resource, target.Resource));
                    }
                }
            }
        }

        bidirectionalGraph.AddEdgeRange(edges);

        XNamespace ns = "http://graphml.graphdrawing.org/xmlns";
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";

        var graphML = new XElement(ns + "graph",
            new XAttribute("id", "EdFi Dependencies"),
            new XAttribute("edgedefault", "directed")
        );

        // Add nodes
        foreach (var vertex in bidirectionalGraph.Vertices)
        {
            graphML.Add(new XElement(ns + "node", new XAttribute("id", vertex)));
        }

        // Add edges (simplified: lower order → higher order)
        // Add edges
        foreach (var edge in bidirectionalGraph.Edges)
        {
            graphML.Add(new XElement(ns + "edge",
                new XAttribute("source", edge.Source),
                new XAttribute("target", edge.Target)));
        }

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(ns + "graphml",
                new XAttribute(XNamespace.Xmlns + "xsi", xsi),
                new XAttribute(xsi + "schemaLocation",
                    "http://graphml.graphdrawing.org/xmlns http://graphml.graphdrawing.org/xmlns/1.0/graphml.xsd"),
                graphML
            )
        );

        using var ms = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false), // No BOM
            Indent = true
        };

        using (var writer = XmlWriter.Create(ms, settings))
        {
            doc.WriteTo(writer);
            writer.Flush();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    internal class ResourceItem
    {
        public string Resource { get; set; }
        public int Order { get; set; }
        public List<string> Operations { get; set; }
    }

}
