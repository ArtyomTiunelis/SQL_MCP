using System.ComponentModel;
using Microsoft.Data.SqlClient;
using ModelContextProtocol.Server;

namespace SQL_MCP.Tools;

[McpServerToolType]
public class DefinitionTools(SqlConnectionFactory connectionFactory, ServerSettings settings)
{
    [McpServerTool, Description("Returns the full T-SQL definition of a stored procedure, view, or function. Use filter to retrieve only lines surrounding a specific keyword instead of the full body.")]
    public async Task<string> get_object_definition(
        [Description("Exact name of the object (e.g., 'sp_GetUserData', 'vw_Orders', 'dbo.fn_GetDiscount').")] string object_name,
        [Description("Object type — required. Use 'procedure', 'view', or 'function'.")] string type,
        [Description("Keyword to find within the body — returns only the ±15 lines around each match instead of the full definition.")] string? filter = null,
        [Description("Database to query (leave empty for default).")] string? catalog = null)
    {
        var typeFilter = type.ToLowerInvariant() switch
        {
            "procedure" => "o.type IN ('P','PC')",
            "view"      => "o.type = 'V'",
            "function"  => "o.type IN ('FN','IF','TF')",
            _           => null
        };

        if (typeFilter is null)
            return $"Unknown type '{type}'. Use 'procedure', 'view', or 'function'.";

        var sql = $"""
            SELECT sm.definition
            FROM sys.sql_modules sm
            INNER JOIN sys.objects o ON sm.object_id = o.object_id
            WHERE {typeFilter}
              AND (o.name = @name OR CONCAT(SCHEMA_NAME(o.schema_id), '.', o.name) = @name)
            """;

        await using var connection = connectionFactory.CreateConnection(catalog);
        try
        {
        await connection.OpenAsync();
        await using var command = connectionFactory.CreateCommand(sql, connection);
        command.Parameters.AddWithValue("@name", object_name);

        var result = await command.ExecuteScalarAsync();
        if (result is null)
            return $"No {type} found with name '{object_name}'.";

        return ToolHelpers.ProcessDefinition(result.ToString()!, filter, object_name, settings);
        }
        catch (SqlException ex) { return ToolHelpers.FormatSqlError(ex, nameof(get_object_definition)); }
    }
}
