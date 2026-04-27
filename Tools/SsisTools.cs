using System.ComponentModel;
using System.Text;
using Microsoft.Data.SqlClient;
using ModelContextProtocol.Server;

namespace SQL_MCP.Tools;

[McpServerToolType]
public class SsisTools(SqlConnectionFactory connectionFactory, ServerSettings settings)
{
    [McpServerTool, Description("Lists all SSIS catalog folders across all configured servers.")]
    public async Task<string> list_ssis_folders()
    {
        const string sql = """
            SELECT name, description, created_time
            FROM catalog.folders
            ORDER BY name
            """;

        return await QueryAllSsisServers(sql, null, reader =>
            $"{reader.GetString(0)} | {(reader.IsDBNull(1) ? "" : reader.GetString(1))} | {reader.GetDateTimeOffset(2):yyyy-MM-dd}",
            "Folder", nameof(list_ssis_folders));
    }

    [McpServerTool, Description("Lists SSIS projects in a catalog folder.")]
    public async Task<string> list_ssis_projects(
        [Description("Folder name to list projects from. Use list_ssis_folders to discover.")] string folder_name)
    {
        const string sql = """
            SELECT p.name, p.description, p.last_deployed_time
            FROM catalog.projects p
            INNER JOIN catalog.folders f ON p.folder_id = f.folder_id
            WHERE f.name = @folder
            ORDER BY p.name
            """;

        return await QueryAllSsisServers(sql,
            cmd => { cmd.Parameters.AddWithValue("@folder", folder_name); },
            reader => $"{reader.GetString(0)} | {(reader.IsDBNull(1) ? "" : reader.GetString(1))} | {reader.GetDateTimeOffset(2):yyyy-MM-dd HH:mm}",
            "Project", nameof(list_ssis_projects));
    }

    [McpServerTool, Description("Lists SSIS packages in a project.")]
    public async Task<string> list_ssis_packages(
        [Description("Project name.")] string project_name,
        [Description("Folder name.")] string folder_name)
    {
        const string sql = """
            SELECT pkg.name, pkg.description, pkg.package_format_version
            FROM catalog.packages pkg
            INNER JOIN catalog.projects pr ON pkg.project_id = pr.project_id
            INNER JOIN catalog.folders f ON pr.folder_id = f.folder_id
            WHERE pr.name = @project AND f.name = @folder
            ORDER BY pkg.name
            """;

        return await QueryAllSsisServers(sql,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@project", project_name);
                cmd.Parameters.AddWithValue("@folder", folder_name);
            },
            reader => $"{reader.GetString(0)} | {(reader.IsDBNull(1) ? "" : reader.GetString(1))} | v{reader.GetInt32(2)}",
            "Package", nameof(list_ssis_packages));
    }

    [McpServerTool, Description("Returns parameters for an SSIS package.")]
    public async Task<string> get_ssis_package_parameters(
        [Description("Package name (e.g., 'MyPackage.dtsx').")] string package_name,
        [Description("Project name.")] string project_name,
        [Description("Folder name.")] string folder_name)
    {
        const string sql = """
            SELECT
                op.parameter_name,
                op.data_type,
                op.design_default_value,
                op.sensitive,
                op.required,
                op.value_set,
                op.description
            FROM catalog.object_parameters op
            INNER JOIN catalog.projects pr ON op.project_id = pr.project_id
            INNER JOIN catalog.folders f ON pr.folder_id = f.folder_id
            WHERE op.object_name = @package
              AND pr.name = @project
              AND f.name = @folder
              AND op.object_type = 30
            ORDER BY op.parameter_name
            """;

        return await QueryAllSsisServers(sql,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@package", package_name);
                cmd.Parameters.AddWithValue("@project", project_name);
                cmd.Parameters.AddWithValue("@folder", folder_name);
            },
            reader =>
            {
                var name = reader.GetString(0);
                var type = reader.GetString(1);
                var def = reader.IsDBNull(2) ? "(null)" : reader.GetValue(2).ToString()!;
                var sensitive = reader.GetBoolean(3) ? "SENSITIVE" : "";
                var required = reader.GetBoolean(4) ? "REQUIRED" : "";
                var desc = reader.IsDBNull(6) ? "" : reader.GetString(6);
                var flags = string.Join(" ", new[] { sensitive, required }.Where(s => s.Length > 0));
                return $"{name} ({type}) default={def}{(flags.Length > 0 ? " " + flags : "")} {desc}".TrimEnd();
            },
            "Parameter", nameof(get_ssis_package_parameters));
    }

    [McpServerTool, Description("Searches SSIS package names across all servers for a keyword.")]
    public async Task<string> search_ssis_packages(
        [Description("Keyword to search for in package names.")] string search_term)
    {
        const string sql = """
            SELECT f.name AS folder, pr.name AS project, pkg.name AS package
            FROM catalog.packages pkg
            INNER JOIN catalog.projects pr ON pkg.project_id = pr.project_id
            INNER JOIN catalog.folders f ON pr.folder_id = f.folder_id
            WHERE pkg.name LIKE @term
            ORDER BY f.name, pr.name, pkg.name
            """;

        return await QueryAllSsisServers(sql,
            cmd => { cmd.Parameters.AddWithValue("@term", $"%{search_term}%"); },
            reader => $"{reader.GetString(0)}/{reader.GetString(1)}/{reader.GetString(2)}",
            "Package", nameof(search_ssis_packages));
    }

    [McpServerTool, Description("Returns recent execution history for an SSIS package.")]
    public async Task<string> get_ssis_execution_history(
        [Description("Package name.")] string package_name,
        [Description("Project name.")] string project_name,
        [Description("Folder name.")] string folder_name)
    {
        var sql = $"""
            SELECT TOP ({settings.SsisExecutionHistoryLimit})
                e.execution_id,
                e.status,
                e.start_time,
                e.end_time,
                e.caller_name
            FROM catalog.executions e
            WHERE e.package_name = @package
              AND e.project_name = @project
              AND e.folder_name = @folder
            ORDER BY e.start_time DESC
            """;

        return await QueryAllSsisServers(sql,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@package", package_name);
                cmd.Parameters.AddWithValue("@project", project_name);
                cmd.Parameters.AddWithValue("@folder", folder_name);
            },
            reader =>
            {
                var id = reader.GetInt64(0);
                var status = reader.GetInt32(1) switch
                {
                    1 => "Created",
                    2 => "Running",
                    3 => "Canceled",
                    4 => "Failed",
                    5 => "Pending",
                    6 => "Ended unexpectedly",
                    7 => "Succeeded",
                    9 => "Completing",
                    _ => $"Unknown({reader.GetInt32(1)})"
                };
                var start = reader.IsDBNull(2) ? "�" : reader.GetDateTimeOffset(2).ToString("yyyy-MM-dd HH:mm:ss");
                var end = reader.IsDBNull(3) ? "�" : reader.GetDateTimeOffset(3).ToString("yyyy-MM-dd HH:mm:ss");
                var caller = reader.GetString(4);
                return $"#{id} {status} {start} -> {end} {caller}";
            },
            "Execution", nameof(get_ssis_execution_history));
    }

    [McpServerTool, Description("Returns error messages for a specific SSIS execution.")]
    public async Task<string> get_ssis_execution_errors(
        [Description("Execution ID from get_ssis_execution_history.")] long execution_id,
        [Description("Server label: 'MainServer' or 'SsisServer'. Identifies which server the execution ran on.")] string server_label)
    {
        const string sql = """
            SELECT
                em.message_time,
                em.message_source_name,
                em.message
            FROM catalog.event_messages em
            WHERE em.operation_id = @execId
              AND em.message_type = 120
            ORDER BY em.message_time
            """;

        var sb = new StringBuilder();
        sb.AppendLine($"Errors for execution #{execution_id} on [{server_label}]:");
        sb.AppendLine(new string('-', 80));

        foreach (var (label, conn) in connectionFactory.CreateSsisConnections())
        {
            if (!string.Equals(label, server_label, StringComparison.OrdinalIgnoreCase))
            {
                conn.Dispose();
                continue;
            }

            try
            {
                await conn.OpenAsync();
                await using var cmd = connectionFactory.CreateCommand(sql, conn);
                cmd.Parameters.AddWithValue("@execId", execution_id);
                await using var reader = await cmd.ExecuteReaderAsync();

                int count = 0;
                while (await reader.ReadAsync() && count < settings.ListCap)
                {
                    count++;
                    var time = reader.GetDateTimeOffset(0).ToString("yyyy-MM-dd HH:mm:ss");
                    var source = reader.GetString(1);
                    var msg = reader.GetString(2);
                    sb.AppendLine($"[{time}] [{source}] {msg}");
                }

                if (count == 0)
                    sb.AppendLine("No error messages found for this execution.");
            }
            catch (SqlException ex)
            {
                sb.AppendLine($"[{label}] Error: {ex.Message}");
            }
            finally
            {
                await conn.DisposeAsync();
            }

            return sb.ToString();
        }

        return $"Server '{server_label}' not found. Use 'MainServer' or 'SsisServer'.";
    }

    private async Task<string> QueryAllSsisServers(
        string sql,
        Action<SqlCommand>? addParams,
        Func<SqlDataReader, string> formatRow,
        string entityName,
        string operation)
    {
        var sb = new StringBuilder();
        int totalRows = 0;

        foreach (var (label, conn) in connectionFactory.CreateSsisConnections())
        {
            try
            {
                await conn.OpenAsync();
                await using var cmd = connectionFactory.CreateCommand(sql, conn);
                addParams?.Invoke(cmd);
                await using var reader = await cmd.ExecuteReaderAsync();

                var rows = new List<string>();
                while (await reader.ReadAsync() && rows.Count < settings.ListCap)
                {
                    rows.Add(formatRow(reader));
                }

                if (rows.Count > 0)
                {
                    sb.AppendLine($"[{label}]");
                    foreach (var row in rows)
                        sb.AppendLine($"  {row}");
                    sb.AppendLine();
                    totalRows += rows.Count;
                }
            }
            catch (SqlException ex) when (ex.Number == 208 || ex.Number == 4060)
            {
                // SSISDB doesn't exist on this server � skip silently
            }
            catch (SqlException ex)
            {
                sb.AppendLine($"[{label}] Error: {ex.Message}");
            }
            finally
            {
                await conn.DisposeAsync();
            }
        }

        if (totalRows == 0)
            return $"No SSIS {entityName.ToLowerInvariant()}s found.";

        return sb.ToString();
    }
}
