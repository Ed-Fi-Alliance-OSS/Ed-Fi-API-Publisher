namespace EdFi.Tools.ApiPublisher.Connections.Api.DependencyResolution;

// NOTE: While currently this is only supported by the API as source, it should probably be treated as a global interface
// (moved to Core project).

public interface ISourceResourceItemProvider
{
    Task<(bool success, string? itemJson)> TryGetResourceItemAsync(string resourceItemUrl);
}
