using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Xml.Linq;
using EdFi.Tools.ApiPublisher.Core.ApiClientManagement;
using log4net;

// ReSharper disable InconsistentNaming

namespace EdFi.Tools.ApiPublisher.Core.Dependencies
{
    public class EdFiV3ApiResourceDependencyProvider : IResourceDependencyProvider
    {
        private readonly ILog _logger = LogManager.GetLogger(typeof(EdFiV3ApiResourceDependencyProvider));
        
        public async Task<IDictionary<string, string[]>> GetDependenciesByResourcePathAsync(
            EdFiApiClient edfiApiClient,
            bool includeDescriptors)
        {
            var (dependencyGraphML, ns) = await GetResourceDependenciesAsync(edfiApiClient).ConfigureAwait(false);
            
            var graph = dependencyGraphML.Element(ns + "graph");

            if (graph == null)
            {
                throw new Exception("Unable to obtain graph element from dependency metadata response.");
            }
            
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

        private async Task<(XElement, XNamespace)> GetResourceDependenciesAsync(EdFiApiClient edFiApiClient)
        {
            string dependenciesRequestUri = $"metadata/{edFiApiClient.DataManagementApiSegment}/dependencies";

            // Get the resource dependencies from the target
            _logger.Info($"Getting dependencies from API at {edFiApiClient.HttpClient.BaseAddress}{dependenciesRequestUri}...");
            
            var dependencyRequest = new HttpRequestMessage(HttpMethod.Get, dependenciesRequestUri);
            dependencyRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/graphml"));
            var dependencyResponse = await edFiApiClient.HttpClient.SendAsync(dependencyRequest).ConfigureAwait(false);

            string dependencyResponseContent = await dependencyResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!dependencyResponse.IsSuccessStatusCode)
            {
                _logger.Error($"Ed-Fi ODS API request for dependencies to '{dependencyRequest.RequestUri}' returned '{dependencyResponse.StatusCode}' with content:{Environment.NewLine}{dependencyResponseContent}");
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
                _logger.Error($"Unable to parse dependency response as GraphML: {dependencyResponseContent}{Environment.NewLine}{ex}");
                throw new Exception("Resource dependencies could not be obtained.");
            }
        }
    }
}