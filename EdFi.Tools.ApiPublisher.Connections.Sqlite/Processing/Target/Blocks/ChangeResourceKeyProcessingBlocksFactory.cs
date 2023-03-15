using EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Target.Messages;
using Microsoft.Data.Sqlite;

namespace EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Target.Blocks;

public class ChangeResourceKeyProcessingBlocksFactory : SqlLiteProcessingBlocksFactoryBase<KeyChangesJsonMessage>
{
    public ChangeResourceKeyProcessingBlocksFactory(Func<SqliteConnection> createConnection)
        : base(createConnection) { }

    protected override string TableSuffix
    {
        get => "KeyChanges";
    }
}
