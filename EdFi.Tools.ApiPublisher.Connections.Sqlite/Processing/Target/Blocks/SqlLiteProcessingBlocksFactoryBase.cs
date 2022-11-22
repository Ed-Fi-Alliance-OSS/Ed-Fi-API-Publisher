// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Threading.Tasks.Dataflow;
using System.Xml.Serialization;
using EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Target.Messages;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Blocks;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using log4net;
using Microsoft.Data.Sqlite;

namespace EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Target.Blocks;

public abstract class SqlLiteProcessingBlocksFactoryBase<TProcessDataMessage> : IProcessingBlocksFactory<TProcessDataMessage>
    where TProcessDataMessage : ResourceJsonMessage, new()
{
    private readonly Func<SqliteConnection> _createConnection;

    private readonly ILog _logger = LogManager.GetLogger(typeof(UpsertProcessingBlocksFactory));

    protected SqlLiteProcessingBlocksFactoryBase(Func<SqliteConnection> createConnection)
    {
        _createConnection = createConnection;
    }

    protected abstract string TableSuffix { get; }

    private readonly ConcurrentDictionary<string, (string, string)> _tableTupleByResourceUrl = new(StringComparer.OrdinalIgnoreCase);

    public (ITargetBlock<TProcessDataMessage>, ISourceBlock<ErrorItemMessage>) CreateProcessingBlocks(
        CreateBlocksRequest createBlocksRequest)
    {
        var block = new TransformManyBlock<ResourceJsonMessage, ErrorItemMessage>(
            async msg =>
            {
                try
                {
                    var (schema, table) = _tableTupleByResourceUrl.GetOrAdd(
                        msg.ResourceUrl,
                        url =>
                        {
                            string[] parts = msg.ResourceUrl.Split('/', StringSplitOptions.RemoveEmptyEntries);

                            string parsedSchema = parts[0].Replace('-', '_');
                            string parsedTable = parts[1];
                                
                            using var connection = _createConnection();
                            var cmd = connection.CreateCommand();

                            // Create the table to hold the data
                            cmd.CommandText = $@"
                                CREATE TABLE {parsedSchema}__{parsedTable}_{TableSuffix} (
                                id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                                json TEXT NOT NULL);";

                            connection.Open();
                            cmd.ExecuteNonQuery();

                            return (parsedSchema, parsedTable);
                        });

                    await using var connection = _createConnection();

                    var cmd = connection.CreateCommand();

                    cmd.CommandText = $@"
                    INSERT INTO {schema}__{table}_{TableSuffix} (Json)
                    VALUES ($json)
";
                    cmd.Parameters.AddWithValue("$json", msg.Json);

                    connection.Open();
                    cmd.ExecuteNonQuery();

                    // Success - no errors to publish
                    return Enumerable.Empty<ErrorItemMessage>();
                }
                catch (Exception ex)
                {
                    // Error is final, log it and indicate failure for processing
                    _logger.Error(
                        $"{msg.ResourceUrl}: An error occurred while writing the JSON to Sqlite database.{Environment.NewLine}{ex}");

                    // Publish the failed data
                    var error = new ErrorItemMessage
                    {
                        ResourceUrl = msg.ResourceUrl,
                        ResponseContent = msg.Json.Substring(0, 200) + "...",
                    };

                    return new[] { error };
                }
            },
            new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 1 });

        return (block, block);
    }

    public IEnumerable<TProcessDataMessage> CreateProcessDataMessages(
        StreamResourcePageMessage<TProcessDataMessage> message,
        string json)
    {
        yield return new TProcessDataMessage
        {
            ResourceUrl = message.ResourceUrl,
            Json = json
        };
    }
}