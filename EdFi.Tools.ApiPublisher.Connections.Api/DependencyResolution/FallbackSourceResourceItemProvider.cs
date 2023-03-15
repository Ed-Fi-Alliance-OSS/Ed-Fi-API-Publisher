using EdFi.Tools.ApiPublisher.Connections.Api.Processing.Target.Blocks;

namespace EdFi.Tools.ApiPublisher.Connections.Api.DependencyResolution;

/// <summary>
/// Implements a resource item provider for use when the source does not have the capability.
/// </summary>
public class FallbackSourceResourceItemProvider : ISourceResourceItemProvider
{
    public Task<(bool success, string? itemJson)> TryGetResourceItemAsync(string resourceItemUrl) => Task.FromResult((false, null as string));
}
