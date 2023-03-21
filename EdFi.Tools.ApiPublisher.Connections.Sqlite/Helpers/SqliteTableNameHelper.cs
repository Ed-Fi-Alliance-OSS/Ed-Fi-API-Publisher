// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.Tools.ApiPublisher.Connections.Sqlite.Helpers;

public static class SqliteTableNameHelper
{
    public static (string schema, string table, string context) ParseDetailsFromResourcePath(string resourcePath)
    {
        string[] parts = resourcePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        string parsedSchema = parts[0].Replace('-', '_');
        string parsedTable = parts[1];

        string context = parts.Length > 2
            ? parts[2]
            : null;

        string parsedTableSuffix = string.IsNullOrEmpty(context)
            ? null
            : $"_{context}";

        return (parsedSchema, parsedTable, parsedTableSuffix);
    }
}
