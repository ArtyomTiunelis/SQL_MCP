using System.ComponentModel;
using System.Text;
using Microsoft.Data.SqlClient;
using ModelContextProtocol.Server;

namespace SQL_MCP.Tools;

[McpServerToolType]
public class SsisTools(SqlConnectionFactory connectionFactory, ServerSettings settings)
{
    [McpServerTool, Description("Lists the configured SSIS server labels that can be passed to server_label parameters.")]
    public string list_ssis_servers()
    {
        var labels = connectionFactory.CreateSsisConnections()
            .Select(c => { c.Connection.Dispose(); return c.Label; })
            .ToList();
        return $"Available servers: {string.Join(", ", labels)}";
    }

    [McpServerTool, Description("Resolves a package name (or partial name) to the full folder/project/package/server_label needed by other endpoints. Use this first when you only know the package name.")]
    public async Task<string> resolve_ssis_package(
        [Description("Full or partial package name to search for.")] string package_name)
    {
        const string sql = """
            SELECT f.name AS folder_name, pr.name AS project_name, pkg.name AS package_name
            FROM catalog.packages pkg
            INNER JOIN catalog.projects pr ON pkg.project_id = pr.project_id
            INNER JOIN catalog.folders f ON pr.folder_id = f.folder_id
            WHERE pkg.name LIKE @term
            ORDER BY f.name, pr.name, pkg.name
            """;

        var sb = new StringBuilder();
        int total = 0;

        foreach (var (label, conn) in connectionFactory.CreateSsisConnections())
        {
            try
            {
                await conn.OpenAsync();
                await using var cmd = connectionFactory.CreateCommand(sql, conn);
                cmd.Parameters.AddWithValue("@term", package_name.Contains('%') ? package_name : $"%{package_name}%");
                await using var reader = await cmd.ExecuteReaderAsync();

                while (await reader.ReadAsync() && total < settings.ListCap)
                {
                    total++;
                    var folder = reader.GetString(0);
                    var project = reader.GetString(1);
                    var package = reader.GetString(2);
                    sb.AppendLine($"  folder_name: {folder}");
                    sb.AppendLine($"  project_name: {project}");
                    sb.AppendLine($"  package_name: {package}");
                    sb.AppendLine($"  server_label: {label}");
                    if (total < settings.ListCap)
                        sb.AppendLine();
                }
            }
            catch (SqlException ex) when (ex.Number == 208 || ex.Number == 4060)
            {
                // SSISDB doesn't exist on this server - skip silently
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

        if (total == 0)
            return $"No packages matching '{package_name}' found on any server.";

        return $"Found {total} match(es):\n\n{sb}";
    }

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

    [McpServerTool, Description("Returns project-level parameters for an SSIS project.")]
    public async Task<string> get_ssis_project_parameters(
        [Description("Project name.")] string project_name,
        [Description("Folder name.")] string folder_name)
    {
        const string sql = """
            SELECT op.parameter_name, op.data_type, op.design_default_value,
                   op.sensitive, op.required, op.value_set, op.description
            FROM catalog.object_parameters op
            INNER JOIN catalog.projects pr ON op.project_id = pr.project_id
            INNER JOIN catalog.folders f ON pr.folder_id = f.folder_id
            WHERE pr.name = @project AND f.name = @folder
              AND op.object_type = 20
            ORDER BY op.parameter_name
            """;

        return await QueryAllSsisServers(sql,
            cmd =>
            {
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
            "Parameter", nameof(get_ssis_project_parameters));
    }

    [McpServerTool, Description("Returns environment references and their variables for an SSIS project. Shows which environments are linked and what values they provide.")]
    public async Task<string> get_ssis_environment_references(
        [Description("Project name.")] string project_name,
        [Description("Folder name.")] string folder_name)
    {
        const string refSql = """
            SELECT er.reference_id, er.environment_folder_name, er.environment_name, er.reference_type
            FROM catalog.environment_references er
            INNER JOIN catalog.projects pr ON er.project_id = pr.project_id
            INNER JOIN catalog.folders f ON pr.folder_id = f.folder_id
            WHERE pr.name = @project AND f.name = @folder
            ORDER BY er.environment_name
            """;

        const string varSql = """
            SELECT ev.name, ev.type, ev.value, ev.sensitive, ev.description
            FROM catalog.environment_variables ev
            INNER JOIN catalog.environments e ON ev.environment_id = e.environment_id
            INNER JOIN catalog.folders ef ON e.folder_id = ef.folder_id
            WHERE e.name = @env AND ef.name = @envFolder
            ORDER BY ev.name
            """;

        var sb = new StringBuilder();
        int total = 0;

        foreach (var (label, conn) in connectionFactory.CreateSsisConnections())
        {
            try
            {
                await conn.OpenAsync();

                var refs = new List<(long Id, string Folder, string Name, string Type)>();
                await using (var cmd = connectionFactory.CreateCommand(refSql, conn))
                {
                    cmd.Parameters.AddWithValue("@project", project_name);
                    cmd.Parameters.AddWithValue("@folder", folder_name);
                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        var envFolder = reader.IsDBNull(1) ? folder_name : reader.GetString(1);
                        refs.Add((reader.GetInt64(0), envFolder, reader.GetString(2),
                                  reader.GetString(3) == "R" ? "Relative" : "Absolute"));
                    }
                }

                foreach (var (_, envFolder, envName, refType) in refs)
                {
                    total++;
                    sb.AppendLine($"[{label}] {envFolder}/{envName} ({refType})");

                    await using var varCmd = connectionFactory.CreateCommand(varSql, conn);
                    varCmd.Parameters.AddWithValue("@env", envName);
                    varCmd.Parameters.AddWithValue("@envFolder", envFolder);
                    await using var varReader = await varCmd.ExecuteReaderAsync();

                    while (await varReader.ReadAsync())
                    {
                        var varName = varReader.GetString(0);
                        var varType = varReader.GetString(1);
                        var varValue = varReader.GetBoolean(3)
                            ? "****"
                            : (varReader.IsDBNull(2) ? "(null)" : varReader.GetValue(2).ToString()!);
                        var varDesc = varReader.IsDBNull(4) ? "" : varReader.GetString(4);
                        sb.AppendLine($"  {varName} ({varType}) = {varValue}{(varDesc.Length > 0 ? " | " + varDesc : "")}");
                    }

                    sb.AppendLine();
                }
            }
            catch (SqlException ex) when (ex.Number == 208 || ex.Number == 4060) { }
            catch (SqlException ex)
            {
                sb.AppendLine($"[{label}] Error: {ex.Message}");
            }
            finally
            {
                await conn.DisposeAsync();
            }
        }

        if (total == 0)
            return $"No environment references found for project '{project_name}' in folder '{folder_name}'.";

        return sb.ToString();
    }

    [McpServerTool, Description("Searches SSIS package names across all servers for a keyword. Returns folder/project/package per match with server labels - use these values directly in other endpoints.")]
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

        var sb = new StringBuilder();
        int total = 0;

        foreach (var (label, conn) in connectionFactory.CreateSsisConnections())
        {
            try
            {
                await conn.OpenAsync();
                await using var cmd = connectionFactory.CreateCommand(sql, conn);
                cmd.Parameters.AddWithValue("@term", $"%{search_term}%");
                await using var reader = await cmd.ExecuteReaderAsync();

                while (await reader.ReadAsync() && total < settings.ListCap)
                {
                    total++;
                    sb.AppendLine($"  [{label}] {reader.GetString(0)}/{reader.GetString(1)}/{reader.GetString(2)}");
                }
            }
            catch (SqlException ex) when (ex.Number == 208 || ex.Number == 4060) { }
            catch (SqlException ex)
            {
                sb.AppendLine($"  [{label}] Error: {ex.Message}");
            }
            finally
            {
                await conn.DisposeAsync();
            }
        }

        if (total == 0)
            return $"No SSIS packages matching '{search_term}' found.";

        return $"Found {total} package(s):\n{sb}";
    }

    [McpServerTool, Description("Returns recent execution history for an SSIS package. Set latest_only to get the most recent execution with inline error messages.")]
    public async Task<string> get_ssis_execution_history(
        [Description("Package name.")] string package_name,
        [Description("Project name.")] string project_name,
        [Description("Folder name.")] string folder_name,
        [Description("If true, returns only the latest execution and includes error messages inline if it failed.")] bool latest_only = false)
    {
        var limit = latest_only ? 1 : settings.SsisExecutionHistoryLimit;
        var sql = $"""
            SELECT TOP ({limit})
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

        const string errorSql = """
            SELECT em.message_time, em.message_source_name, em.message
            FROM catalog.event_messages em
            WHERE em.operation_id = @execId AND em.message_type = 120
            ORDER BY em.message_time
            """;

        var sb = new StringBuilder();
        int totalRows = 0;

        foreach (var (label, conn) in connectionFactory.CreateSsisConnections())
        {
            try
            {
                await conn.OpenAsync();
                await using var cmd = connectionFactory.CreateCommand(sql, conn);
                cmd.Parameters.AddWithValue("@package", package_name);
                cmd.Parameters.AddWithValue("@project", project_name);
                cmd.Parameters.AddWithValue("@folder", folder_name);
                await using var reader = await cmd.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    totalRows++;
                    var id = reader.GetInt64(0);
                    var statusCode = reader.GetInt32(1);
                    var status = FormatStatus(statusCode);
                    var start = reader.IsDBNull(2) ? "-" : reader.GetDateTimeOffset(2).ToString("yyyy-MM-dd HH:mm:ss");
                    var end = reader.IsDBNull(3) ? "-" : reader.GetDateTimeOffset(3).ToString("yyyy-MM-dd HH:mm:ss");
                    var caller = reader.GetString(4);
                    sb.AppendLine($"[{label}] #{id} {status} {start} -> {end} {caller}");

                    if (latest_only && statusCode is 4 or 6)
                    {
                        await using var errCmd = connectionFactory.CreateCommand(errorSql, conn);
                        errCmd.Parameters.AddWithValue("@execId", id);
                        await using var errReader = await errCmd.ExecuteReaderAsync();
                        int errCount = 0;
                        while (await errReader.ReadAsync() && errCount < settings.ListCap)
                        {
                            errCount++;
                            var time = errReader.GetDateTimeOffset(0).ToString("yyyy-MM-dd HH:mm:ss");
                            var source = errReader.GetString(1);
                            var msg = errReader.GetString(2);
                            sb.AppendLine($"  ERROR [{time}] [{source}] {msg}");
                        }
                        if (errCount == 0)
                            sb.AppendLine("  No error messages recorded.");
                    }
                }
            }
            catch (SqlException ex) when (ex.Number == 208 || ex.Number == 4060) { }
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
            return "No SSIS executions found.";

        return sb.ToString();
    }

    [McpServerTool, Description("Returns error messages for a specific SSIS execution.")]
    public async Task<string> get_ssis_execution_errors(
        [Description("Execution ID from get_ssis_execution_history.")] long execution_id,
        [Description("Server label from list_ssis_servers. Identifies which server the execution ran on.")] string server_label)
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

        var available = string.Join(", ", connectionFactory.CreateSsisConnections()
            .Select(c => { c.Connection.Dispose(); return c.Label; }));
        return $"Server '{server_label}' not found. Available: {available}";
    }

    private static string FormatStatus(int status) => status switch
    {
        1 => "Created",
        2 => "Running",
        3 => "Canceled",
        4 => "Failed",
        5 => "Pending",
        6 => "Ended unexpectedly",
        7 => "Succeeded",
        9 => "Completing",
        _ => $"Unknown({status})"
    };

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
                // SSISDB doesn't exist on this server - skip silently
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
