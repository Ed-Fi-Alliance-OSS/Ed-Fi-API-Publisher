using System.Threading.Tasks;
using EdFi.Tools.ApiPublisher.Core.Configuration;

namespace EdFi.Tools.ApiPublisher.Core.Versioning;

public interface IEdFiVersionsChecker
{
    /// <summary>
    /// Checks Ed-Fi API and Standard versions for compatibility
    /// </summary>
    /// <param name="configuration"></param>
    /// <returns></returns>
    Task CheckApiVersionsAsync(ChangeProcessorConfiguration configuration);
}