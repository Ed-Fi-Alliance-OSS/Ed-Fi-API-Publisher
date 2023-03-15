using EdFi.Tools.ApiPublisher.Core.Versioning;
using Microsoft.Data.Sqlite;

namespace EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Source.Versioning;

public class SqliteSourceCurrentChangeVersionProvider : ISourceCurrentChangeVersionProvider
{
    private readonly Func<SqliteConnection> _createConnection;

    public SqliteSourceCurrentChangeVersionProvider(Func<SqliteConnection> createConnection)
    {
        _createConnection = createConnection;
    }
    
    public async Task<long?> GetCurrentChangeVersionAsync()
    {
        await using var connection = _createConnection();

        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT TOP 1 CurrentChangeVersion FROM PublishingMetadata";

        await connection.OpenAsync();
        var rawValue = await cmd.ExecuteScalarAsync();

        if (rawValue != null)
        {
            return Convert.ToInt64(rawValue);
        }

        return null;
    }
}
