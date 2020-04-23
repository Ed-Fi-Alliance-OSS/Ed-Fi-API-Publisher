using System.Collections.Generic;
using System.Net.Http;

namespace EdFi.Tools.ApiPublisher.Core.Dependencies
{
    public interface IResourceDependencyProvider
    {
        IDictionary<string, string[]> GetDependenciesByResourcePath(HttpClient httpClient, bool includeDescriptors);
    }
}