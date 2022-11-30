using EdFi.Tools.ApiPublisher.Core.Capabilities;

namespace EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Source.Capabilities;

public class SqliteSourceCapabilities : ISourceCapabilities
{
    public Task<bool> SupportsKeyChangesAsync(string probeResourceKey) => Task.FromResult(true);

    public Task<bool> SupportsDeletesAsync(string probeResourceKey) => Task.FromResult(true);

    public bool SupportsGetItemById
    {
        get => false;
    }
}
