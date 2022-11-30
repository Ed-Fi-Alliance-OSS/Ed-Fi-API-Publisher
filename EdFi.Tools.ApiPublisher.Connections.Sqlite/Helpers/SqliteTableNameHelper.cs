namespace EdFi.Tools.ApiPublisher.Connections.Sqlite.Helpers;

public static class SqliteTableNameHelper
{
    public static (string schema, string table, string? context) ParseDetailsFromResourcePath(string resourcePath)
    {
        string[] parts = resourcePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        string parsedSchema = parts[0].Replace('-', '_');
        string parsedTable = parts[1];

        string? context = parts.Length > 2
            ? parts[2]
            : null;

        string? parsedTableSuffix = string.IsNullOrEmpty(context)
            ? null
            : $"_{context}";

        return (parsedSchema, parsedTable, parsedTableSuffix);
    }
}
