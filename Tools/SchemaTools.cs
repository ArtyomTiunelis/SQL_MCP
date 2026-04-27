using System.ComponentModel;
using System.ComponentModel;
using System.Text;
using Microsoft.Data.SqlClient;
using ModelContextProtocol.Server;
using SQL_MCP;

namespace SQL_MCP.Tools;

[McpServerToolType]
public class SchemaTools(SqlConnectionFactory connectionFactory, ServerSettings settings)
{
    [McpServerTool, Description("Returns column names, data types, max lengths, and nullability for a table.")]
    public async Task<string> get_table_schema(
        [Description("Exact table name (e.g., 'Users' or 'dbo.Orders').")] string table_name,
        [Description("Database to query (leave empty for default).")] string? catalog = null)
    {
        var (schema, table) = ParseTableName(table_name);

        const string sql = """
            SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_NAME = @table
              AND (@schema IS NULL OR TABLE_SCHEMA = @schema)
            ORDER BY ORDINAL_POSITION
            """;

        await using var connection = connectionFactory.CreateConnection(catalog);
        try
        {
        await connection.OpenAsync();
        await using var command = connectionFactory.CreateCommand(sql, connection);
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@schema", (object?)schema ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync();
        var sb = new StringBuilder();
        sb.AppendLine($"Schema for '{table_name}':");
        sb.AppendLine($"{"Column",-40} {"Type",-20} {"Max Length",-12} {"Nullable"}");
        sb.AppendLine(new string('-', 82));

        bool hasRows = false;
        while (await reader.ReadAsync())
        {
            hasRows = true;
            var maxLen = reader.IsDBNull(2) ? "n/a" : reader.GetInt32(2).ToString();
            sb.AppendLine($"{reader.GetString(0),-40} {reader.GetString(1),-20} {maxLen,-12} {reader.GetString(3)}");
        }

        return hasRows ? sb.ToString() : $"No table found with name '{table_name}'.";
        }
        catch (SqlException ex) { return ToolHelpers.FormatSqlError(ex, nameof(get_table_schema)); }
    }

    [McpServerTool, Description("Returns all foreign key relationships for a table — both as parent (referenced by others) and child (referencing others). Use this to write accurate JOINs.")]
    public async Task<string> get_table_dependencies(
        [Description("Exact table name to check for foreign key dependencies.")] string table_name,
        [Description("Database to query (leave empty for default).")] string? catalog = null)
    {
        var (_, table) = ParseTableName(table_name);

        const string sql = """
            SELECT
                fk.name AS fk_name,
                parent_schema.name + '.' + parent_table.name AS parent_table,
                parent_col.name AS parent_column,
                ref_schema.name + '.' + ref_table.name AS referenced_table,
                ref_col.name AS referenced_column
            FROM sys.foreign_keys fk
            INNER JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
            INNER JOIN sys.tables parent_table ON fkc.parent_object_id = parent_table.object_id
            INNER JOIN sys.schemas parent_schema ON parent_table.schema_id = parent_schema.schema_id
            INNER JOIN sys.columns parent_col ON fkc.parent_object_id = parent_col.object_id AND fkc.parent_column_id = parent_col.column_id
            INNER JOIN sys.tables ref_table ON fkc.referenced_object_id = ref_table.object_id
            INNER JOIN sys.schemas ref_schema ON ref_table.schema_id = ref_schema.schema_id
            INNER JOIN sys.columns ref_col ON fkc.referenced_object_id = ref_col.object_id AND fkc.referenced_column_id = ref_col.column_id
            WHERE parent_table.name = @table OR ref_table.name = @table
            ORDER BY parent_table.name, fk.name
            """;

        await using var connection = connectionFactory.CreateConnection(catalog);
        try
        {
        await connection.OpenAsync();
        await using var command = connectionFactory.CreateCommand(sql, connection);
        command.Parameters.AddWithValue("@table", table);

        await using var reader = await command.ExecuteReaderAsync();
        var sb = new StringBuilder();
        sb.AppendLine($"Foreign key dependencies for '{table_name}':");
        sb.AppendLine($"{"FK Name",-40} {"Parent Table",-30} {"Parent Col",-25} {"Referenced Table",-30} {"Ref Col"}");
        sb.AppendLine(new string('-', 140));

        bool hasRows = false;
        while (await reader.ReadAsync())
        {
            hasRows = true;
            sb.AppendLine($"{reader.GetString(0),-40} {reader.GetString(1),-30} {reader.GetString(2),-25} {reader.GetString(3),-30} {reader.GetString(4)}");
        }

        return hasRows ? sb.ToString() : $"No foreign key dependencies found for '{table_name}'.";
        }
        catch (SqlException ex) { return ToolHelpers.FormatSqlError(ex, nameof(get_table_dependencies)); }
    }

    [McpServerTool, Description("Finds all tables containing a column matching a name or partial name (e.g., 'StatusID'). Use this to locate where a concept lives across the database.")]
    public async Task<string> search_columns(
        [Description("Column name or partial name to search for (e.g., 'StatusID', 'Customer').")] string column_name,
        [Description("Database to query (leave empty for default).")] string? catalog = null)
    {
        const string sql = """
            SELECT
                COUNT(*) OVER() AS total,
                TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME, DATA_TYPE, IS_NULLABLE
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE COLUMN_NAME LIKE @name
            ORDER BY TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME
            """;

        await using var connection = connectionFactory.CreateConnection(catalog);
        try
        {
        await connection.OpenAsync();
        await using var command = connectionFactory.CreateCommand(sql, connection);
        command.Parameters.AddWithValue("@name", $"%{column_name}%");

        await using var reader = await command.ExecuteReaderAsync();
        var sb = new StringBuilder();
        sb.AppendLine($"Tables containing column matching '{column_name}':");
        sb.AppendLine($"{"Schema",-20} {"Table",-40} {"Column",-40} {"Type",-20} {"Nullable"}");
        sb.AppendLine(new string('-', 130));

        int count = 0; int total = 0;
        while (await reader.ReadAsync() && count < settings.ListCap)
        {
            total = reader.GetInt32(0);
            count++;
            sb.AppendLine($"{reader.GetString(1),-20} {reader.GetString(2),-40} {reader.GetString(3),-40} {reader.GetString(4),-20} {reader.GetString(5)}");
        }

        if (count == 0) return $"No columns found matching '{column_name}'.";
        ToolHelpers.AppendCapNotice(sb, count, total, $"a more specific column_name");
        return sb.ToString();
        }
        catch (SqlException ex) { return ToolHelpers.FormatSqlError(ex, nameof(search_columns)); }
    }

    [McpServerTool, Description("Returns all indexes on a table — key columns, uniqueness, and clustering. Check this before querying large tables to avoid full scans.")]
    public async Task<string> get_indexes(
        [Description("Exact table name (e.g., 'Orders' or 'dbo.Orders').")] string table_name,
        [Description("Database to query (leave empty for default).")] string? catalog = null)
    {
        var (_, table) = ParseTableName(table_name);

        const string sql = """
            SELECT
                i.name AS index_name,
                i.type_desc,
                i.is_unique,
                i.is_primary_key,
                STRING_AGG(c.name, ', ') WITHIN GROUP (ORDER BY ic.key_ordinal) AS columns
            FROM sys.indexes i
            INNER JOIN sys.tables t ON i.object_id = t.object_id
            INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
            INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            WHERE t.name = @table
              AND ic.is_included_column = 0
            GROUP BY i.name, i.type_desc, i.is_unique, i.is_primary_key
            ORDER BY i.is_primary_key DESC, i.is_unique DESC, i.name
            """;

        await using var connection = connectionFactory.CreateConnection(catalog);
        try
        {
        await connection.OpenAsync();
        await using var command = connectionFactory.CreateCommand(sql, connection);
        command.Parameters.AddWithValue("@table", table);

        await using var reader = await command.ExecuteReaderAsync();
        var sb = new StringBuilder();
        sb.AppendLine($"Indexes on '{table_name}':");
        sb.AppendLine($"{"Index Name",-50} {"Type",-15} {"Unique",-8} {"PK",-6} {"Key Columns"}");
        sb.AppendLine(new string('-', 110));

        int count = 0;
        while (await reader.ReadAsync())
        {
            count++;
            sb.AppendLine($"{reader.GetString(0),-50} {reader.GetString(1),-15} {reader.GetBoolean(2),-8} {reader.GetBoolean(3),-6} {reader.GetString(4)}");
        }

        return count == 0 ? $"No indexes found for '{table_name}'." : sb.ToString();
        }
        catch (SqlException ex) { return ToolHelpers.FormatSqlError(ex, nameof(get_indexes)); }
    }

    [McpServerTool, Description("Returns all triggers on a table with their full T-SQL body. Triggers contain hidden write logic not visible in stored procedures — always check.")]
    public async Task<string> get_triggers(
        [Description("Exact table name (e.g., 'Orders' or 'dbo.Orders').")] string table_name,
        [Description("Keyword to find within trigger bodies — returns only the \u00b115 lines around each match instead of full bodies.")] string? filter = null,
        [Description("Database to query (leave empty for default).")] string? catalog = null)
    {
        var (_, table) = ParseTableName(table_name);

        const string sql = """
            SELECT
                tr.name AS trigger_name,
                CASE WHEN tr.is_instead_of_trigger = 1 THEN 'INSTEAD OF' ELSE 'AFTER' END AS trigger_type,
                (
                    SELECT STRING_AGG(te.type_desc, ', ')
                    FROM sys.trigger_events te
                    WHERE te.object_id = tr.object_id
                ) AS events,
                sm.definition
            FROM sys.triggers tr
            INNER JOIN sys.tables t ON tr.parent_id = t.object_id
            INNER JOIN sys.sql_modules sm ON tr.object_id = sm.object_id
            WHERE t.name = @table
            ORDER BY tr.name
            """;

        await using var connection = connectionFactory.CreateConnection(catalog);
        try
        {
        await connection.OpenAsync();
        await using var command = connectionFactory.CreateCommand(sql, connection);
        command.Parameters.AddWithValue("@table", table);

        await using var reader = await command.ExecuteReaderAsync();
        var sb = new StringBuilder();

        int count = 0;
        while (await reader.ReadAsync())
        {
            count++;
            sb.AppendLine($"-- Trigger: {reader.GetString(0)}  [{reader.GetString(1)} {reader.GetString(2)}]");
            sb.AppendLine(ToolHelpers.ProcessDefinition(reader.GetString(3), filter, reader.GetString(0), settings));
            sb.AppendLine();
        }

        return count == 0 ? $"No triggers found on '{table_name}'." : sb.ToString();
        }
        catch (SqlException ex) { return ToolHelpers.FormatSqlError(ex, nameof(get_triggers)); }
    }

    private static (string? schema, string table) ParseTableName(string tableName)
    {
        var parts = tableName.Split('.', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : (null, parts[0]);
    }
}

