// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Serilog;

// ReSharper disable InconsistentNaming

namespace EdFi.Tools.ApiPublisher.Core.Dependencies
{
    public class ResourceDependencyProvider : IResourceDependencyProvider
    {
        private readonly IGraphMLDependencyMetadataProvider _graphMLDependencyMetadataProvider;

        private readonly ILogger _logger = Log.ForContext(typeof(ResourceDependencyProvider));

        public ResourceDependencyProvider(IGraphMLDependencyMetadataProvider graphMlDependencyMetadataProvider)
        {
            _graphMLDependencyMetadataProvider = graphMlDependencyMetadataProvider;
        }

        public async Task<IDictionary<string, string[]>> GetDependenciesByResourcePathAsync(bool includeDescriptors)
        {
            var (dependencyGraphML, ns) = await _graphMLDependencyMetadataProvider.GetDependencyMetadataAsync().ConfigureAwait(false);

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

            var dependenciesByResource = edges.GroupBy(e => e.Attribute("target")?.Value, e => e.Attribute("source")?.Value)
                .ToDictionary(g => g.Key, g => g.Select(x => x).ToArray(), StringComparer.OrdinalIgnoreCase);

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
                dependenciesByResource.Add(resourcePath, Array.Empty<string>());
            }

            return dependenciesByResource;
        }
    }
}
