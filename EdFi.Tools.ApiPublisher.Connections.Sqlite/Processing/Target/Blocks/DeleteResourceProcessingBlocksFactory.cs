using EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Target.Messages;
using Microsoft.Data.Sqlite;

namespace EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Target.Blocks;

public class DeleteResourceProcessingBlocksFactory : SqlLiteProcessingBlocksFactoryBase<DeletesJsonMessage>
{
    public DeleteResourceProcessingBlocksFactory(Func<SqliteConnection> createConnection)
        : base(createConnection) { }

    protected override string TableSuffix
    {
        get => "Deletes";
    }
}
