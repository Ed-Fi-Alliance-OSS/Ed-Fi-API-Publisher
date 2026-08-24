// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Sqlite.Helpers;
using EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Target.Messages;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Helpers;
using EdFi.Tools.ApiPublisher.Core.Processing;
using EdFi.Tools.ApiPublisher.Core.Processing.Blocks;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using Microsoft.Data.Sqlite;
using Polly;
using Polly.Contrib.WaitAndRetry;
using Serilog;
using Serilog.Events;
using System.Collections.Concurrent;
using System.Threading.Tasks.Dataflow;

namespace EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Target.Blocks;

public abstract class SqlLiteProcessingBlocksFactoryBase<TProcessDataMessage> : IProcessingBlocksFactory<TProcessDataMessage>
    where TProcessDataMessage : ResourceJsonMessage, new()
{
    private readonly Func<SqliteConnection> _createConnection;

    private readonly ILogger _logger = Log.ForContext(typeof(UpsertProcessingBlocksFactory));

    protected SqlLiteProcessingBlocksFactoryBase(Func<SqliteConnection> createConnection)
    {
        _createConnection = createConnection;
    }

    protected abstract string TableSuffix { get; }

    private readonly ConcurrentDictionary<string, (string schema, string table, string tableSuffix)> _tableTupleByResourceUrl
        = new(StringComparer.OrdinalIgnoreCase);

    public (ITargetBlock<TProcessDataMessage>, ISourceBlock<ErrorItemMessage>) CreateProcessingBlocks(
        CreateBlocksRequest createBlocksRequest)
    {
        // Unlike the API target blocks, every message this block receives carries a WHOLE PAGE of items
        // (see CreateProcessDataMessages below), so the item-denominated capacity must be converted to
        // page messages -- applying it directly would admit that many whole pages (at shipped defaults,
        // roughly 500 pages against a documented ceiling of ~5,500 items).
        int resolvedItemCapacity = createBlocksRequest.Options.ResolvedProcessingBlockBoundedCapacity;

        int boundedCapacity = resolvedItemCapacity == -1
            ? DataflowBlockOptions.Unbounded
            : Math.Max(1, resolvedItemCapacity / Math.Max(1, createBlocksRequest.Options.StreamingPageSize));

        var block = new TransformManyBlock<ResourceJsonMessage, ErrorItemMessage>(
            async msg =>
            {
                Options options = createBlocksRequest.Options;

                try
                {
                    var (schema, table, tableSuffix) = _tableTupleByResourceUrl.GetOrAdd(
                        msg.ResourceUrl,
                        url =>
                        {
                            var (parsedSchema, parsedTable, parsedTableSuffix) = SqliteTableNameHelper.ParseDetailsFromResourcePath(msg.ResourceUrl);

                            using var connection = _createConnection();
                            var cmd = connection.CreateCommand();

                            // Create the table to hold the data
                            cmd.CommandText = $@"
                                CREATE TABLE {parsedSchema}__{parsedTable}{parsedTableSuffix} (
                                id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                                json TEXT NOT NULL);";

                            connection.Open();
                            cmd.ExecuteNonQuery();

                            return (parsedSchema, parsedTable, parsedTableSuffix);
                        });

                    await using var connection = _createConnection();

                    var cmd = connection.CreateCommand();

                    cmd.CommandText = $@"
                    INSERT INTO {schema}__{table}{tableSuffix} (Json)
                    VALUES ($json)
";
                    cmd.Parameters.AddWithValue("$json", msg.Json);

                    connection.Open();

                    int attempts = 0;

                    var delay = Backoff.ExponentialBackoff(
                        TimeSpan.FromMilliseconds(options.RetryStartingDelayMilliseconds),
                        options.MaxRetryAttempts);

                    var sqlInsertResult = await Policy
                        .Handle<Exception>()
                        .WaitAndRetryAsync(delay, (result, ts, retryAttempt, ctx) =>
                        {
                            _logger.Warning($"{msg.ResourceUrl}: INSERT to Sqlite table '{schema}__{table}{tableSuffix}' failed with error '{result.Message}'). Retrying... (retry #{retryAttempt} of {options.MaxRetryAttempts} with {ts.TotalSeconds:N1}s delay)");

                            return Task.CompletedTask;
                        })
                        .ExecuteAsync(async (ctx, ct) =>
                        {
                            attempts++;

                            if (attempts > 1)
                            {
                                if (_logger.IsEnabled(LogEventLevel.Debug))
                                {
                                    _logger.Debug($"{msg.ResourceUrl}: INSERT to Sqlite table '{schema}__{table}{tableSuffix}' attempt #{attempts}.");
                                }
                            }

                            return await cmd.ExecuteNonQueryAsync(ct);
                        }, new Context(), CancellationToken.None);

                    // Success - no errors to publish
                    return Enumerable.Empty<ErrorItemMessage>();
                }
                catch (Exception ex)
                {
                    // Error is final, log it and indicate failure for processing
                    _logger.Error($"{msg.ResourceUrl}: An error occurred while writing the JSON to Sqlite database.", ex);

                    // Publish the failed data
                    var error = new ErrorItemMessage
                    {
                        ResourceUrl = msg.ResourceUrl,
                        ResponseContent = msg.Json is { Length: > 200 } ? msg.Json[..200] + "..." : msg.Json,
                        Exception = ex
                    };

                    return new[] { error };
                }
            },
            new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = 1,

                // The single-threaded SQLite writer is the slowest consumer in the codebase; bound its buffer
                // so a slow write path exerts backpressure on source page streaming (see APIPUB-112)
                BoundedCapacity = boundedCapacity
            });

        return (block, block);
    }

    public IEnumerable<TProcessDataMessage> CreateProcessDataMessages(
        StreamResourcePageMessage<TProcessDataMessage> message,
        TextReader jsonReader,
        Action<int> reportTopLevelItemCount)
    {
        // EXEMPTION (APIPUB-134): this connector stores whole pages as TEXT rows in SQLite, so the page is
        // deliberately buffered to a string here rather than streamed item-by-item.
        string json = jsonReader.ReadToEnd();

        // The API source handler still needs the top-level item count for final-page continuation when
        // downloading to SQLite; run the streaming counting pass only when a caller asks for the count.
        reportTopLevelItemCount?.Invoke(JsonHelpers.CountTopLevelArrayItems(json));

        yield return new TProcessDataMessage
        {
            ResourceUrl = message.ResourceUrl,
            Json = json
        };
    }
}
