using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Xml.Linq;
using EdFi.Tools.ApiPublisher.Core.Extensions;
using EdFi.Tools.ApiPublisher.Core.Processing;
using log4net;

namespace EdFi.Tools.ApiPublisher.Core.Dependencies
{
    public class EdFiV3ApiResourceDependencyProvider : IResourceDependencyProvider
    {
        private readonly ILog _logger = LogManager.GetLogger(typeof(EdFiV3ApiResourceDependencyProvider));
        
        public IDictionary<string, string[]> GetDependenciesByResourcePath(HttpClient httpClient, bool includeDescriptors)
        {
            if (!TryGetResourceDependencies(httpClient, out var dependencyGraphML, out var ns))
            {
                throw new Exception("Resource dependencies could not be obtained.");
            }

            var graph = dependencyGraphML.Element(ns + "graph");
            
            var edges = graph.Elements(ns + "edge");

            if (!includeDescriptors)
            {
                // Skip all descriptor edges, using conventions
                edges = edges.Where(e => e.Attribute("source")?.Value?.EndsWith("Descriptors") == false);
            }

            var dependenciesByResource = edges
                .GroupBy(e => e.Attribute("target")?.Value, e => e.Attribute("source")?.Value)
                .ToDictionary(
                    g => g.Key, 
                    g => g.Select(x => x).ToArray(), 
                    StringComparer.OrdinalIgnoreCase);

            var independentNodes = graph.Elements(ns + "node")
                .Select(n => n.Attribute("id")?.Value)
                .Where(n => !dependenciesByResource.ContainsKey(n));

            if (!includeDescriptors)
            {
                // Exclude descriptors, by convention
                independentNodes = independentNodes.Where(n => !n.EndsWith("Descriptors"));
            }
            
            // Add the independent entities with no dependencies
            foreach (string resourcePath in independentNodes)
            {
                dependenciesByResource.Add(resourcePath, new string[0]);
            }

            return dependenciesByResource;
        }

        private bool TryGetResourceDependencies(HttpClient httpClient, out XElement dependencyGraphML, out XNamespace ns)
        {
            dependencyGraphML = null;
            ns = null;
            
            // Get the resource dependencies from the target
            _logger.Info($"Getting dependencies from API at {httpClient.BaseAddress}...");
            
            var dependencyRequest = new HttpRequestMessage(HttpMethod.Get, $"metadata/{EdFiApiConstants.DataManagementApiSegment}/dependencies");
            dependencyRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/graphml"));
            var dependencyResponse = httpClient.SendAsync(dependencyRequest).GetResultSafely();

            string dependencyResponseContent = dependencyResponse.Content.ReadAsStringAsync().GetResultSafely();

            if (!dependencyResponse.IsSuccessStatusCode)
            {
                _logger.Error($"Ed-Fi ODS API request for dependencies to '{dependencyRequest.RequestUri}' returned '{dependencyResponse.StatusCode}' with content:{Environment.NewLine}{dependencyResponseContent}");
                return false;
            }
            
            ns = "http://graphml.graphdrawing.org/xmlns";

            try
            {
                dependencyGraphML = XElement.Parse(dependencyResponseContent);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Unable to parse dependency response as GraphML: {dependencyResponseContent}{Environment.NewLine}{ex}");
                return false;
            }
        }
    }
}