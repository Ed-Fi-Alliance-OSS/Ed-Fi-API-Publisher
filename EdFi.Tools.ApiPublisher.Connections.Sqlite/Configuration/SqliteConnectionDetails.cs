using EdFi.Tools.ApiPublisher.Core.Configuration;

namespace EdFi.Tools.ApiPublisher.Connections.Sqlite.Configuration;

public class SqliteConnectionDetails : SourceConnectionDetailsBase, ITargetConnectionDetails
{
    public string? File { get; set; }
    
    public override bool IsFullyDefined() => !string.IsNullOrEmpty(File);

    /// <summary>
    /// Indicates that the Sqlite connection information does not need additional resolution.
    /// </summary>
    /// <returns>Always returns <b>false</b>.</returns>
    public override bool NeedsResolution() => false;
}
