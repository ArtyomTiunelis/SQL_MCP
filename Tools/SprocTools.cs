using System.ComponentModel;
using System.ComponentModel;
using System.Text;
using Microsoft.Data.SqlClient;
using ModelContextProtocol.Server;
using SQL_MCP;

namespace SQL_MCP.Tools;

[McpServerToolType]
public class SprocTools(SqlConnectionFactory connectionFactory, ServerSettings settings)
{
    [McpServerTool, Description("Searches all stored procedure, view, and function definitions for a keyword. Use this to trace dependencies or find where a table or column is referenced.")]
    public async Task<string> search_database_code(
        [Description("Keyword, table name, or column name to search for.")] string search_term,
        [Description("Database to query (leave empty for default).")] string? catalog = null)
    {
        const string sql = """
            SELECT
                COUNT(*) OVER() AS total,
                o.type_desc,
                SCHEMA_NAME(o.schema_id) + '.' + o.name AS object_name
            FROM sys.sql_modules sm
            INNER JOIN sys.objects o ON sm.object_id = o.object_id
            WHERE sm.definition LIKE @term
            ORDER BY o.type_desc, o.name
            """;

        await using var connection = connectionFactory.CreateConnection(catalog);
        try
        {
        await connection.OpenAsync();
        await using var command = connectionFactory.CreateCommand(sql, connection);

        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        int total = 0;

        while (await reader.ReadAsync() && rows.Count < settings.ListCap)
        {
            total = reader.GetInt32(0);
            rows.Add($"[{reader.GetString(1)}] {reader.GetString(2)}");
        }

        if (rows.Count == 0)
            return $"No database objects found containing '{search_term}'.";

        var sb = new StringBuilder();
        sb.AppendLine(string.Join("\n", rows));
        ToolHelpers.AppendCapNotice(sb, rows.Count, total, $"search_term with a more specific keyword");
        return sb.ToString();
        }
        catch (SqlException ex) { return ToolHelpers.FormatSqlError(ex, nameof(search_database_code)); }
    }

    [McpServerTool, Description("Returns the parameter signature of a stored procedure (names, types, direction, defaults) without fetching the full body.")]
    public async Task<string> get_sproc_parameters(
        [Description("Exact name of the stored procedure (e.g., 'sp_GetUserData' or 'dbo.sp_ProcessOrder').")] string sproc_name,
        [Description("Database to query (leave empty for default).")] string? catalog = null)
    {
        const string sql = """
            SELECT
                p.name AS parameter_name,
                TYPE_NAME(p.user_type_id) AS data_type,
                p.max_length,
                p.precision,
                p.scale,
                p.is_output,
                p.has_default_value,
                p.default_value
            FROM sys.parameters p
            INNER JOIN sys.objects o ON p.object_id = o.object_id
            WHERE o.type IN ('P', 'PC')
              AND (o.name = @name OR CONCAT(SCHEMA_NAME(o.schema_id), '.', o.name) = @name)
            ORDER BY p.parameter_id
            """;

        await using var connection = connectionFactory.CreateConnection(catalog);
        try
        {
        await connection.OpenAsync();
        await using var command = connectionFactory.CreateCommand(sql, connection);

        await using var reader = await command.ExecuteReaderAsync();
        var sb = new StringBuilder();
        sb.AppendLine($"Parameters for '{sproc_name}':");
        sb.AppendLine($"{"Parameter",-35} {"Type",-20} {"Direction",-10} {"Has Default",-12} {"Default Value"}");
        sb.AppendLine(new string('-', 100));

        int count = 0;
        while (await reader.ReadAsync())
        {
            count++;
            var direction = reader.GetBoolean(5) ? "OUTPUT" : "INPUT";
            var hasDefault = reader.GetBoolean(6);
            var defaultVal = hasDefault && !reader.IsDBNull(7) ? reader.GetValue(7).ToString()! : "";
            sb.AppendLine($"{reader.GetString(0),-35} {reader.GetString(1),-20} {direction,-10} {hasDefault,-12} {defaultVal}");
        }

        return count == 0
            ? $"No stored procedure found with name '{sproc_name}', or it has no parameters."
            : sb.ToString();
        }
        catch (SqlException ex) { return ToolHelpers.FormatSqlError(ex, nameof(get_sproc_parameters)); }
    }
}
