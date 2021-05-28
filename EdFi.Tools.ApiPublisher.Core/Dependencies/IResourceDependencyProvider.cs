using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Core.Dependencies
{
    public interface IResourceDependencyProvider
    {
        Task<IDictionary<string, string[]>> GetDependenciesByResourcePathAsync(HttpClient httpClient, bool includeDescriptors);
    }
}