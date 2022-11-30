using EdFi.Tools.ApiPublisher.Core.Versioning;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;

namespace EdFi.Tools.ApiPublisher.Connections.Sqlite.Metadata.Versioning;

public class SqliteSourceEdFiApiVersionMetadataProvider : ISourceEdFiApiVersionMetadataProvider
{
    private readonly Func<SqliteConnection> _createConnection;

    public SqliteSourceEdFiApiVersionMetadataProvider(Func<SqliteConnection> createConnection)
    {
        _createConnection = createConnection;
    }

    public async Task<JObject?> GetVersionMetadata()
    {
        await using var connection = _createConnection();

        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT SourceVersionMetadata FROM PublishingMetadata";

        await connection.OpenAsync();
        var rawValue = await cmd.ExecuteScalarAsync();

        if (rawValue != null)
        {
            return JObject.Parse((string) rawValue);
        }

        return null;
    }
}
