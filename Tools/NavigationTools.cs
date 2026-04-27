using System.ComponentModel;
using System.ComponentModel;
using System.Text;
using Microsoft.Data.SqlClient;
using ModelContextProtocol.Server;
using SQL_MCP;

namespace SQL_MCP.Tools;

[McpServerToolType]
public class NavigationTools(SqlConnectionFactory connectionFactory, ServerSettings settings)
{
    [McpServerTool, Description("Lists all accessible SQL Server databases. Call this first to discover available catalogs before using the catalog parameter on other tools.")]
    public async Task<string> list_catalogs()
    {
        try
        {
        const string sql = """
            SELECT name, create_date, collation_name
            FROM sys.databases
            WHERE name NOT IN ('master','tempdb','model','msdb')
              AND state_desc = 'ONLINE'
            ORDER BY name
            """;

        await using var connection = connectionFactory.CreateConnection("master");
        await connection.OpenAsync();
        await using var command = connectionFactory.CreateCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var sb = new StringBuilder();
        sb.AppendLine($"{"Database",-40} {"Created",-25} {"Collation"}");
        sb.AppendLine(new string('-', 100));

        int count = 0;
        while (await reader.ReadAsync())
        {
            count++;
            sb.AppendLine($"{reader.GetString(0),-40} {reader.GetDateTime(1),-25:yyyy-MM-dd} {reader.GetString(2)}");
        }

        return count == 0 ? "No user databases found." : sb.ToString();
        }
        catch (SqlException ex) { return ToolHelpers.FormatSqlError(ex, nameof(list_catalogs)); }
    }
    [McpServerTool, Description("Lists all user tables, optionally filtered by schema. Never guess table names — enumerate them first.")]
    public async Task<string> list_tables(
        [Description("Schema to filter by (e.g., 'dbo'). Leave empty for all.")] string? schema = null,
        [Description("Name filter — returns only tables whose name contains this string.")] string? filter = null,
        [Description("Database to query (leave empty for default).")] string? catalog = null)
    {
        const string sql = """
            SELECT COUNT(*) OVER() AS total, TABLE_SCHEMA, TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_TYPE = 'BASE TABLE'
              AND (@schema IS NULL OR TABLE_SCHEMA = @schema)
              AND (@filter IS NULL OR TABLE_NAME LIKE @filter)
            ORDER BY TABLE_SCHEMA, TABLE_NAME
            """;

        await using var connection = connectionFactory.CreateConnection(catalog);
        try
        {
        await connection.OpenAsync();
        await using var command = connectionFactory.CreateCommand(sql, connection);
        command.Parameters.AddWithValue("@schema", (object?)schema ?? DBNull.Value);
        command.Parameters.AddWithValue("@filter", filter is null ? DBNull.Value : (object)$"%{filter}%");

        await using var reader = await command.ExecuteReaderAsync();
        var sb = new StringBuilder();
        var dbLabel = catalog is null ? "" : $" [{catalog}]";
        sb.AppendLine(schema is null ? $"All tables{dbLabel}:" : $"Tables in schema '{schema}'{dbLabel}:");
        sb.AppendLine($"{"Schema",-20} {"Table"}");
        sb.AppendLine(new string('-', 60));

        int count = 0; int total = 0;
        while (await reader.ReadAsync() && count < settings.ListCap)
        {
            total = reader.GetInt32(0);
            count++;
            sb.AppendLine($"{reader.GetString(1),-20} {reader.GetString(2)}");
        }

        if (count == 0) return "No tables found.";
        ToolHelpers.AppendCapNotice(sb, count, total, "filter:\"Invoice\"");
        return sb.ToString();
        }
        catch (SqlException ex) { return ToolHelpers.FormatSqlError(ex, nameof(list_tables)); }
    }

    [McpServerTool, Description("Lists stored procedures, views, and functions, optionally filtered by schema and type.")]
    public async Task<string> list_objects(
        [Description("Schema to filter by (e.g., 'dbo'). Leave empty for all.")] string? schema = null,
        [Description("Type filter: 'procedure', 'view', or 'function'. Leave empty for all.")] string? type = null,
        [Description("Name filter — returns only objects whose name contains this string.")] string? filter = null,
        [Description("Database to query (leave empty for default).")] string? catalog = null)
    {
        var typeFilter = type?.ToLowerInvariant() switch
        {
            "procedure" => "AND o.type IN ('P','PC')",
            "view"      => "AND o.type = 'V'",
            "function"  => "AND o.type IN ('FN','IF','TF')",
            _           => "AND o.type IN ('P','PC','V','FN','IF','TF')"
        };

        var sql = $"""
            SELECT
                COUNT(*) OVER() AS total,
                SCHEMA_NAME(o.schema_id) AS [schema],
                o.name,
                o.type_desc
            FROM sys.objects o
            WHERE 1=1
              {typeFilter}
              AND (@schema IS NULL OR SCHEMA_NAME(o.schema_id) = @schema)
              AND (@filter IS NULL OR o.name LIKE @filter)
            ORDER BY SCHEMA_NAME(o.schema_id), o.type_desc, o.name
            """;

        await using var connection = connectionFactory.CreateConnection(catalog);
        try
        {
        await connection.OpenAsync();
        await using var command = connectionFactory.CreateCommand(sql, connection);
        command.Parameters.AddWithValue("@schema", (object?)schema ?? DBNull.Value);
        command.Parameters.AddWithValue("@filter", filter is null ? DBNull.Value : (object)$"%{filter}%");

        await using var reader = await command.ExecuteReaderAsync();
        var sb = new StringBuilder();
        sb.AppendLine($"{"Schema",-20} {"Type",-30} {"Name"}");
        sb.AppendLine(new string('-', 90));

        int count = 0; int total = 0;
        while (await reader.ReadAsync() && count < settings.ListCap)
        {
            total = reader.GetInt32(0);
            count++;
            sb.AppendLine($"{reader.GetString(1),-20} {reader.GetString(3),-30} {reader.GetString(2)}");
        }

        if (count == 0) return "No objects found.";
        ToolHelpers.AppendCapNotice(sb, count, total, "filter:\"nap\" or type:\"procedure\"");
        return sb.ToString();
        }
        catch (SqlException ex) { return ToolHelpers.FormatSqlError(ex, nameof(list_objects)); }
    }

    [McpServerTool, Description("Lists all user-defined schemas. Call this before list_tables to understand how the database is partitioned.")]
    public async Task<string> list_schemas(
        [Description("Database to query (leave empty for default).")] string? catalog = null)
    {
        const string sql = """
            SELECT s.name AS schema_name, p.name AS owner
            FROM sys.schemas s
            INNER JOIN sys.database_principals p ON s.principal_id = p.principal_id
            WHERE s.name NOT IN (
                'sys','guest','INFORMATION_SCHEMA','db_owner','db_accessadmin',
                'db_securityadmin','db_ddladmin','db_backupoperator','db_datareader',
                'db_datawriter','db_denydatareader','db_denydatawriter'
            )
            ORDER BY s.name
            """;

        await using var connection = connectionFactory.CreateConnection(catalog);
        try
        {
        await connection.OpenAsync();
        await using var command = connectionFactory.CreateCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var sb = new StringBuilder();
        sb.AppendLine($"{"Schema",-30} {"Owner"}");
        sb.AppendLine(new string('-', 50));

        int count = 0;
        while (await reader.ReadAsync())
        {
            count++;
            sb.AppendLine($"{reader.GetString(0),-30} {reader.GetString(1)}");
        }

        return count == 0 ? "No user-defined schemas found." : sb.ToString();
        }
        catch (SqlException ex) { return ToolHelpers.FormatSqlError(ex, nameof(list_schemas)); }
    }
}

