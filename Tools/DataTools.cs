using System.ComponentModel;
using System.Text;
using Microsoft.Data.SqlClient;
using ModelContextProtocol.Server;

namespace SQL_MCP.Tools;

[McpServerToolType]
public class DataTools(SqlConnectionFactory connectionFactory, ServerSettings settings)
{
    [McpServerTool, Description("Returns the TOP 5 rows from a table. Use this to inspect real data formats and enum/status code values.")]
    public async Task<string> get_data_sample(
        [Description("Exact table name to sample.")] string table_name,
        [Description("Database to query (leave empty for default).")] string? catalog = null)
    {
        // Validate table exists before constructing dynamic query to prevent injection
        const string validateSql = """
            SELECT COUNT(1)
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_NAME = @table
              AND TABLE_TYPE = 'BASE TABLE'
            """;

        await using var connection = connectionFactory.CreateConnection(catalog);
        try
        {
        await connection.OpenAsync();

        await using var validateCommand = connectionFactory.CreateCommand(validateSql, connection);
        validateCommand.Parameters.AddWithValue("@table", table_name.Contains('.') ? table_name.Split('.')[1] : table_name);
        var count = (int)(await validateCommand.ExecuteScalarAsync())!;

        if (count == 0)
            return $"No table found with name '{table_name}'.";

        var quotedName = string.Join(".", table_name.Split('.').Select(p => $"[{p.Trim('[', ']')}]"));
        var sampleSql = $"SELECT TOP 5 * FROM {quotedName}";

        await using var command = connectionFactory.CreateCommand(sampleSql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var sb = new StringBuilder();
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        sb.AppendLine($"Sample data from '{table_name}' ({reader.FieldCount} columns):");
        sb.AppendLine(string.Join(" | ", columns));
        sb.AppendLine(new string('-', columns.Sum(c => c.Length + 3)));

        int rowCount = 0;
        while (await reader.ReadAsync())
        {
            rowCount++;
            var values = Enumerable.Range(0, reader.FieldCount)
                .Select(i => reader.IsDBNull(i) ? "NULL" : reader.GetValue(i).ToString()!);
            sb.AppendLine(string.Join(" | ", values));
        }

        if (rowCount == 0)
            sb.AppendLine("(no rows)");

        return sb.ToString();
        }
        catch (SqlException ex) { return ToolHelpers.FormatSqlError(ex, nameof(get_data_sample)); }
    }

    [McpServerTool, Description("Executes a read-only SELECT statement (capped at 200 rows). Only SELECT is permitted — all write and DDL statements are rejected.")]
    public async Task<string> run_query(
        [Description("A valid SELECT statement to execute.")] string query,
        [Description("Database to query (leave empty for default).")] string? catalog = null)
    {
        var trimmed = query.TrimStart();
        if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            return "Only SELECT statements are permitted. The query was rejected.";

        await using var connection = connectionFactory.CreateConnection(catalog);
        try
        {
        await connection.OpenAsync();

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadUncommitted);
        await using var command = new SqlCommand(query, connection, transaction) { CommandTimeout = settings.CommandTimeout };

        await using var reader = await command.ExecuteReaderAsync();

        var sb = new StringBuilder();
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        sb.AppendLine(string.Join(" | ", columns));
        sb.AppendLine(new string('-', columns.Sum(c => c.Length + 3)));

        int rowCount = 0;
        while (await reader.ReadAsync())
        {
            if (++rowCount > 200)
            {
                sb.AppendLine("... (result truncated at 200 rows)");
                break;
            }
            var values = Enumerable.Range(0, reader.FieldCount)
                .Select(i => reader.IsDBNull(i) ? "NULL" : reader.GetValue(i).ToString()!);
            sb.AppendLine(string.Join(" | ", values));
        }

        if (rowCount == 0)
            sb.AppendLine("(no rows)");

        return sb.ToString();
        }
        catch (SqlException ex) { return ToolHelpers.FormatSqlError(ex, nameof(run_query)); }
    }

    [McpServerTool, Description("Returns approximate row counts for all tables using SQL Server statistics. Check this before querying to avoid fetching millions of rows.")]
    public async Task<string> get_row_counts(
        [Description("Schema to filter by (e.g., 'dbo'). Leave empty for all.")] string? schema = null,
        [Description("Database to query (leave empty for default).")] string? catalog = null)
    {
        const string sql = """
            SELECT
                COUNT(*) OVER() AS total,
                s.name AS [schema],
                t.name AS [table],
                SUM(p.rows) AS row_count
            FROM sys.tables t
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            INNER JOIN sys.partitions p ON t.object_id = p.object_id
            WHERE p.index_id IN (0, 1)
              AND (@schema IS NULL OR s.name = @schema)
            GROUP BY s.name, t.name
            ORDER BY SUM(p.rows) DESC
            """;

        await using var connection = connectionFactory.CreateConnection(catalog);
        try
        {
        await connection.OpenAsync();
        await using var command = connectionFactory.CreateCommand(sql, connection);
        command.Parameters.AddWithValue("@schema", (object?)schema ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync();
        var sb = new StringBuilder();
        sb.AppendLine($"{"Schema",-20} {"Table",-50} {"Row Count",15}");
        sb.AppendLine(new string('-', 90));

        int count = 0; int total = 0;
        while (await reader.ReadAsync() && count < settings.ListCap)
        {
            total = reader.GetInt32(0);
            count++;
            sb.AppendLine($"{reader.GetString(1),-20} {reader.GetString(2),-50} {reader.GetInt64(3),15:N0}");
        }

        if (count == 0) return "No tables found.";
        ToolHelpers.AppendCapNotice(sb, count, total, "schema:\"dbo\" to filter by schema");
        return sb.ToString();
        }
        catch (SqlException ex) { return ToolHelpers.FormatSqlError(ex, nameof(get_row_counts)); }
    }
}

