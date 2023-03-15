using System.Collections.Generic;
using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Core.ApiClientManagement;

namespace EdFi.Tools.ApiPublisher.Core.Dependencies
{
    public interface IResourceDependencyProvider
    {
        Task<IDictionary<string, string[]>> GetDependenciesByResourcePathAsync(bool includeDescriptors);
    }
}