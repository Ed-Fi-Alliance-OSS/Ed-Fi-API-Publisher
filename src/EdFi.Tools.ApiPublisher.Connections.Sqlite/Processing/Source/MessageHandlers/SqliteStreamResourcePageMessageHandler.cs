// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Connections.Sqlite.Helpers;
using EdFi.Tools.ApiPublisher.Core.Configuration;
using EdFi.Tools.ApiPublisher.Core.Processing.Handlers;
using EdFi.Tools.ApiPublisher.Core.Processing.Messages;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Serilog;
using Serilog.Events;
using System.Threading.Tasks.Dataflow;

namespace EdFi.Tools.ApiPublisher.Connections.Sqlite.Processing.Source.MessageHandlers;

public class SqliteStreamResourcePageMessageHandler : IStreamResourcePageMessageHandler
{
    private readonly Func<SqliteConnection> _createConnection;

    private readonly ILogger _logger = Log.ForContext(typeof(SqliteStreamResourcePageMessageHandler));

    public SqliteStreamResourcePageMessageHandler(Func<SqliteConnection> createConnection)
    {
        _createConnection = createConnection;
    }

    public async Task<IEnumerable<TProcessDataMessage>> HandleStreamResourcePageAsync<TProcessDataMessage>(
        StreamResourcePageMessage<TProcessDataMessage> message,
        Options options,
        ITargetBlock<ErrorItemMessage> errorHandlingBlock)
    {
        int pageId = int.Parse(message.PartitionFrom ?? throw new NullReferenceException("PartitionFrom is expected on resource page messages for use with the Sqlite connection."));

        try
        {
            var transformedMessages = new List<TProcessDataMessage>();

            if (message.CancellationSource.IsCancellationRequested)
            {
                _logger.Debug(
                    $"{message.ResourceUrl}: Cancellation requested while processing page of source items starting at partition '{pageId}'.");

                return Enumerable.Empty<TProcessDataMessage>();
            }

            if (_logger.IsEnabled(LogEventLevel.Debug))
            {
                _logger.Debug($"{message.ResourceUrl}: Retrieving page items for page '{pageId}'.");
            }

            string json;

            try
            {
                await using var connection = _createConnection();
                var (schema, table, tableSuffix) = SqliteTableNameHelper.ParseDetailsFromResourcePath(message.ResourceUrl);

                var cmd = connection.CreateCommand();
                cmd.CommandText = $"SELECT json FROM {schema}__{table}{tableSuffix} WHERE id = $pageId";
                cmd.Parameters.AddWithValue("$pageId", pageId);

                await connection.OpenAsync();

                json = (string)await cmd.ExecuteScalarAsync(message.CancellationSource.Token).ConfigureAwait(false);

                if (string.IsNullOrEmpty(json))
                {
                    throw new Exception("Sqlite database page contained no content.");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"{message.ResourceUrl}: Unable to obtain Sqlite database source page '{pageId}'.", ex);

                var error = new ErrorItemMessage
                {
                    ResourceUrl = $"{message.ResourceUrl}",
                    Id = pageId.ToString(),
                    Exception = ex,
                };

                // Publish the failure
                errorHandlingBlock.Post(error);

                return Array.Empty<TProcessDataMessage>();
            }

            // Transform the page content to item actions
            try
            {
                transformedMessages.AddRange(message.CreateProcessDataMessages(message, json!));
            }
            catch (JsonReaderException ex)
            {
                // An error occurred while parsing the JSON
                _logger.Error($"{message.ResourceUrl}: JSON parsing of Sqlite database source page '{pageId}' data failed.", ex);

                // Publish the failure
                var error = new ErrorItemMessage
                {
                    ResourceUrl = $"{message.ResourceUrl}",
                    Id = pageId.ToString(),
                    Exception = ex,
                };

                // Publish the failure
                errorHandlingBlock.Post(error);

                return Array.Empty<TProcessDataMessage>();
            }

            return transformedMessages;
        }
        catch (Exception ex)
        {
            _logger.Error($"{message.ResourceUrl}: An unhandled exception occurred during processing:{Environment.NewLine}{ex}");
            throw;
        }
    }
}
