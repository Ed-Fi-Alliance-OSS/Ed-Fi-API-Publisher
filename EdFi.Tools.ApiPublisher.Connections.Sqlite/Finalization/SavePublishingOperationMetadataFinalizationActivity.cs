// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.Tools.ApiPublisher.Core.Finalization;
using EdFi.Tools.ApiPublisher.Core.Metadata;
using Microsoft.Data.Sqlite;

namespace EdFi.Tools.ApiPublisher.Connections.Sqlite.Finalization;

public class SavePublishingOperationMetadataFinalizationActivity : IFinalizationActivity
{
    private readonly IPublishingOperationMetadataCollector _publishingOperationMetadataCollector;
    private readonly Func<SqliteConnection> _createConnection;

    public SavePublishingOperationMetadataFinalizationActivity(
        IPublishingOperationMetadataCollector publishingOperationMetadataCollector,
        Func<SqliteConnection> createConnection)
    {
        _publishingOperationMetadataCollector = publishingOperationMetadataCollector;
        _createConnection = createConnection;
    }
    
    public async Task Execute()
    {
        await using var connection = _createConnection();
        
        await connection.OpenAsync();

        await CreatePublishingMetadataTableAsync();
        await CreateResourceItemCountTableAsync();

        var metadata = _publishingOperationMetadataCollector.GetMetadata();

        await SavePublishingOperationMetadataAsync();
        await SaveResourceItemCountsAsync();

        async Task CreatePublishingMetadataTableAsync()
        {
            var cmd = connection.CreateCommand();

            cmd.CommandText = @"
            CREATE TABLE PublishingMetadata(
                CurrentChangeVersion INT NULL,
                SourceVersionMetadata TEXT NULL,
                TargetVersionMetadata TEXT NULL,
                MinChangeVersion BIGINT NULL,
                MaxChangeVersion BIGINT NULL
            );";

            await cmd.ExecuteNonQueryAsync();
        }

        async Task CreateResourceItemCountTableAsync()
        {
            var cmd = connection.CreateCommand();

            cmd.CommandText = @"
            CREATE TABLE ResourceItemCount(
                ResourcePath NVARCHAR(200),
                ItemCount INTEGER NOT NULL
            );";

            await cmd.ExecuteNonQueryAsync();
        }

        async Task SavePublishingOperationMetadataAsync()
        {
            var cmd = connection.CreateCommand();

            cmd.CommandText = $@"
            INSERT INTO PublishingMetadata(CurrentChangeVersion, SourceVersionMetadata, TargetVersionMetadata, MinChangeVersion, MaxChangeVersion)
            VALUES ($changeVersion, $sourceVersionMetadata, $targetVersionMetadata, $minChangeVersion, $maxChangeVersion);";

            cmd.Parameters.AddWithValue("$changeVersion", metadata.CurrentChangeVersion ?? (object) DBNull.Value);
            cmd.Parameters.AddWithValue("$sourceVersionMetadata", metadata.SourceVersionMetadata?.ToString() ?? (object) DBNull.Value);
            cmd.Parameters.AddWithValue("$targetVersionMetadata", metadata.TargetVersionMetadata?.ToString() ?? (object) DBNull.Value);
            cmd.Parameters.AddWithValue("$minChangeVersion", metadata.ChangeWindow?.MinChangeVersion ?? (object) DBNull.Value);
            cmd.Parameters.AddWithValue("$maxChangeVersion", metadata.ChangeWindow?.MaxChangeVersion ?? (object) DBNull.Value);

            await cmd.ExecuteNonQueryAsync();
        }

        async Task SaveResourceItemCountsAsync()
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO ResourceItemCount(ResourcePath, ItemCount) VALUES ($resourcePath, $itemCount);";
            cmd.Parameters.Add(new SqliteParameter("$resourcePath", SqliteType.Text));
            cmd.Parameters.Add(new SqliteParameter("$itemCount", SqliteType.Integer));

            foreach (var kvp in metadata.ResourceItemCountByPath)
            {
                cmd.Parameters["$resourcePath"].Value = kvp.Key;
                cmd.Parameters["$itemCount"].Value = kvp.Value;
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }
}
