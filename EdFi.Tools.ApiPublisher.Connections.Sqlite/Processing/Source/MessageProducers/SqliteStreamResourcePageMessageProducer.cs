using System.Data;
using System.Threading.Tasks.Dataflow;
using EdFi.Tools.ApiPublisher.Connections.Sqlite.Helpers;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Counting;
using EdFi.Tools.ApiPublisher.Core.Processing.Handlers;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using log4net;
using Microsoft.Data.Sqlite;

namespace EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Source.MessageProducers;

public class SqliteStreamResourcePageMessageProducer : IStreamResourcePageMessageProducer
{
    private readonly ISourceTotalCountProvider _sourceTotalCountProvider;
    private readonly Func<SqliteConnection> _createConnection;

    private readonly ILog _logger = LogManager.GetLogger(typeof(SqliteStreamResourcePageMessageProducer));
    
    public SqliteStreamResourcePageMessageProducer(
        ISourceTotalCountProvider sourceTotalCountProvider,
        Func<SqliteConnection> createConnection)
    {
        _sourceTotalCountProvider = sourceTotalCountProvider;
        _createConnection = createConnection;
    }
    
    public async Task<IEnumerable<StreamResourcePageMessage<TProcessDataMessage>>> ProduceMessagesAsync<TProcessDataMessage>(
        StreamResourceMessage message,
        Options options,
        ITargetBlock<ErrorItemMessage> errorHandlingBlock,
        Func<StreamResourcePageMessage<TProcessDataMessage>, string, IEnumerable<TProcessDataMessage>> createProcessDataMessages,
        CancellationToken cancellationToken)
    {
        // Get total count of items in source resource for change window (if applicable)
        var (totalCountSuccess, totalCount) = await _sourceTotalCountProvider.TryGetTotalCountAsync(
            message.ResourceUrl,
            options,
            message.ChangeWindow,
            errorHandlingBlock,
            cancellationToken);
        
        if (!totalCountSuccess)
        {
            // Allow processing to continue without performing additional work on this resource.
            return Enumerable.Empty<StreamResourcePageMessage<TProcessDataMessage>>();
        }

        _logger.Info($"{message.ResourceUrl}: Total count = {totalCount}");

        if (totalCount == 0)
        {
            return Enumerable.Empty<StreamResourcePageMessage<TProcessDataMessage>>();
        }
        
        await using var connection = _createConnection();

        var (schema, table, tableSuffix) = SqliteTableNameHelper.ParseDetailsFromResourcePath(message.ResourceUrl);
        
        var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT id FROM {schema}__{table}{tableSuffix} ORDER BY id";
        
        await connection.OpenAsync();
        
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.CloseConnection, cancellationToken);

        var pageMessages = new List<StreamResourcePageMessage<TProcessDataMessage>>();

        while (reader.Read())
        {
            var pageMessage = new StreamResourcePageMessage<TProcessDataMessage>()
            {
                // Resource-specific context
                ResourceUrl = message.ResourceUrl,
                PostAuthorizationFailureRetry = message.PostAuthorizationFailureRetry,

                // Use key set paging strategy properties
                PartitionFrom = reader.GetInt32("id").ToString(),

                // Global processing context
                ChangeWindow = message.ChangeWindow,
                CreateProcessDataMessages = createProcessDataMessages,
                CancellationSource = message.CancellationSource,
            };

            pageMessages.Add(pageMessage);
        }

        return pageMessages;
    }
}
