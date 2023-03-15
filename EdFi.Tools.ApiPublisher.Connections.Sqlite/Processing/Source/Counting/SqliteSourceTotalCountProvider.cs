using System.Threading.Tasks.Dataflow;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Counting;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using Microsoft.Data.Sqlite;

namespace EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Source.Counting;

public class SqliteSourceTotalCountProvider : ISourceTotalCountProvider
{
    private readonly Func<SqliteConnection> _createConnection;

    public SqliteSourceTotalCountProvider(Func<SqliteConnection> createConnection)
    {
        _createConnection = createConnection;
    }
    
    public async Task<(bool, long)> TryGetTotalCountAsync(
        string resourceUrl,
        Options options,
        ChangeWindow? changeWindow,
        ITargetBlock<ErrorItemMessage> errorHandlingBlock,
        CancellationToken cancellationToken)
    {
        await using var connection = _createConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        
        var cmd = connection.CreateCommand();

        cmd.CommandText = @"SELECT ItemCount FROM ResourceItemCount WHERE ResourcePath = $resourcePath";
        cmd.Parameters.AddWithValue("$resourcePath", resourceUrl);

        long count = (long) (cmd.ExecuteScalar() ?? -1);

        return (count >= 0, count);
    }
}
