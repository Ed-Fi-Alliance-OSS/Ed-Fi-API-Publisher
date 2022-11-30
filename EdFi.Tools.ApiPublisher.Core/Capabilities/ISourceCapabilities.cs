using System.Threading.Tasks;

namespace EdFi.Tools.ApiPublisher.Core.Capabilities;

public interface ISourceCapabilities
{
    /// <summary>
    /// Indicates whether the data source supports retrieving key changes, using the supplied resource key (if necessary) to probe
    /// for determining the capability.
    /// </summary>
    /// <param name="probeResourceKey"></param>
    /// <returns></returns>
    Task<bool> SupportsKeyChangesAsync(string probeResourceKey);

    /// <summary>
    /// Indicates whether the data source supports retrieving the keys of deleted items, using the supplied resource key (if necessary) to probe
    /// for determining the capability.
    /// </summary>
    /// <param name="probeResourceKey"></param>
    /// <returns></returns>
    Task<bool> SupportsDeletesAsync(string probeResourceKey);

    /// <summary>
    /// Indicates whether the source connection support retrieving items by id.
    /// </summary>
    public bool SupportsGetItemById { get; }
}