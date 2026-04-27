using System.ComponentModel;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using ModelContextProtocol.Server;

namespace SQL_MCP.Tools;

[McpServerToolType]
public class SsisPackageDefinitionTools(SqlConnectionFactory connectionFactory, ServerSettings settings)
{
    private static readonly XNamespace DtsNs = "www.microsoft.com/SqlServer/Dts";
    private static readonly XNamespace SqlTaskNs = "www.microsoft.com/sqlserver/dts/tasks/sqltask";

    [McpServerTool, Description("Returns a summarized view of an SSIS package: tasks, data flows, precedence constraints, and connection managers parsed from the package XML.")]
    public async Task<string> get_ssis_package_definition(
        [Description("Package name (e.g., 'MyPackage.dtsx').")] string package_name,
        [Description("Project name.")] string project_name,
        [Description("Folder name.")] string folder_name,
        [Description("Server label: 'MainServer' or 'SsisServer'. Leave empty to search both.")] string? server_label = null)
    {
        var xml = await GetPackageXml(package_name, project_name, folder_name, server_label);
        if (xml.StartsWith("Error") || xml.StartsWith("No ") || xml.StartsWith("Server"))
            return xml;

        var doc = XDocument.Parse(xml);
        var root = doc.Root!;
        var sb = new StringBuilder();

        sb.AppendLine($"=== Package: {package_name} ===");
        sb.AppendLine();

        // Connection Managers
        var connMgrs = root.Elements(DtsNs + "ConnectionManager").ToList();
        if (connMgrs.Count > 0)
        {
            sb.AppendLine("--- Connection Managers ---");
            foreach (var cm in connMgrs)
            {
                var name = GetDtsProperty(cm, "ObjectName");
                var creationName = GetDtsProperty(cm, "CreationName");
                var connStr = cm.Elements(DtsNs + "ObjectData")
                    .Elements()
                    .Attributes("ConnectionString")
                    .FirstOrDefault()?.Value;
                sb.AppendLine($"  [{creationName}] {name}");
                if (connStr is not null)
                    sb.AppendLine($"    ConnectionString: {MaskConnectionString(connStr)}");
            }
            sb.AppendLine();
        }

        // Executables (tasks)
        sb.AppendLine("--- Tasks / Control Flow ---");
        var executables = root.Element(DtsNs + "Executables")?.Elements(DtsNs + "Executable").ToList() ?? [];
        foreach (var exe in executables)
        {
            FormatExecutable(exe, sb, indent: 1);
        }
        sb.AppendLine();

        // Precedence Constraints
        var constraints = root.Element(DtsNs + "PrecedenceConstraints")?.Elements(DtsNs + "PrecedenceConstraint").ToList() ?? [];
        if (constraints.Count > 0)
        {
            sb.AppendLine("--- Precedence Constraints ---");
            foreach (var pc in constraints)
            {
                var from = GetDtsProperty(pc, "From");
                var to = GetDtsProperty(pc, "To");
                var value = GetDtsProperty(pc, "Value") ?? "0"; // default is Success
                var evalOp = GetDtsProperty(pc, "EvalOp") ?? "2"; // default is Constraint
                var label = value switch
                {
                    "0" => "Success",
                    "1" => "Failure",
                    "2" => "Completion",
                    _ => value
                };
                var evalLabel = evalOp switch
                {
                    "1" => $"Expr: {GetDtsProperty(pc, "Expression") ?? "?"}",
                    "3" => $"{label} AND Expr",
                    "4" => $"{label} OR Expr",
                    _ => label // "2" = Constraint only (default)
                };
                sb.AppendLine($"  {ExtractTaskName(from)} --[{evalLabel}]--> {ExtractTaskName(to)}");
            }
            sb.AppendLine();
        }

        var result = sb.ToString();
        if (result.Length > settings.SsisDefinitionCharCap)
        {
            result = result[..settings.SsisDefinitionCharCap] +
                     $"\n\n[Truncated at {settings.SsisDefinitionCharCap:N0} chars. Use get_ssis_dataflow_details or get_ssis_sql_statements for specific sections.]";
        }

        return result;
    }

    [McpServerTool, Description("Returns details of a specific Data Flow task in an SSIS package: source/destination components, transformations, and column mappings.")]
    public async Task<string> get_ssis_dataflow_details(
        [Description("Name of the Data Flow task as shown in get_ssis_package_definition.")] string dataflow_task_name,
        [Description("Package name (e.g., 'MyPackage.dtsx').")] string package_name,
        [Description("Project name.")] string project_name,
        [Description("Folder name.")] string folder_name,
        [Description("Server label: 'MainServer' or 'SsisServer'. Leave empty to search both.")] string? server_label = null)
    {
        var xml = await GetPackageXml(package_name, project_name, folder_name, server_label);
        if (xml.StartsWith("Error") || xml.StartsWith("No ") || xml.StartsWith("Server"))
            return xml;

        var doc = XDocument.Parse(xml);
        var dataFlow = FindExecutableByName(doc.Root!, dataflow_task_name);
        if (dataFlow is null)
            return $"Data Flow task '{dataflow_task_name}' not found in package '{package_name}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"=== Data Flow: {dataflow_task_name} ===");
        sb.AppendLine();

        // Pipeline components are in ObjectData/pipeline/components/component
        var pipeline = dataFlow.Descendants("pipeline").FirstOrDefault();
        if (pipeline is null)
        {
            // Try with namespace
            pipeline = dataFlow.Descendants(DtsNs + "pipeline").FirstOrDefault();
        }

        if (pipeline is null)
            return $"No pipeline data found in Data Flow task '{dataflow_task_name}'.";

        var components = pipeline.Descendants("component").ToList();
        if (components.Count == 0)
            components = pipeline.Descendants(DtsNs + "component").ToList();

        foreach (var comp in components)
        {
            var name = comp.Attribute("name")?.Value ?? "(unnamed)";
            var contactInfo = comp.Attribute("contactInfo")?.Value ?? "";
            var componentType = comp.Attribute("componentClassID")?.Value ?? "";

            // Determine component kind from contactInfo or classID
            var kind = categorizeComponent(contactInfo, componentType);

            sb.AppendLine($"  [{kind}] {name}");

            // Extract SQL command if present
            var sqlProp = comp.Descendants("property")
                .FirstOrDefault(p => string.Equals(p.Attribute("name")?.Value, "SqlCommand", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(p.Attribute("name")?.Value, "OpenRowset", StringComparison.OrdinalIgnoreCase));
            if (sqlProp is not null && !string.IsNullOrWhiteSpace(sqlProp.Value))
            {
                sb.AppendLine($"    SQL: {sqlProp.Value.Trim()}");
            }

            // Extract column mappings from input/output columns
            var inputs = comp.Descendants("inputColumn").ToList();
            var outputs = comp.Descendants("outputColumn").ToList();
            if (outputs.Count > 0)
            {
                sb.AppendLine($"    Output columns ({outputs.Count}): {string.Join(", ", outputs.Take(10).Select(c => c.Attribute("name")?.Value ?? "?"))}");
                if (outputs.Count > 10) sb.AppendLine($"    ... and {outputs.Count - 10} more");
            }

            sb.AppendLine();
        }

        // Paths (data flow connections between components)
        var paths = pipeline.Descendants("path").ToList();
        if (paths.Count == 0)
            paths = pipeline.Descendants(DtsNs + "path").ToList();

        if (paths.Count > 0)
        {
            sb.AppendLine("  --- Data Flow Paths ---");
            foreach (var path in paths)
            {
                var startId = path.Attribute("startId")?.Value ?? "?";
                var endId = path.Attribute("endId")?.Value ?? "?";
                var pathName = path.Attribute("name")?.Value ?? "";
                sb.AppendLine($"    {pathName}: {startId} --> {endId}");
            }
        }

        var result = sb.ToString();
        if (result.Length > settings.SsisDefinitionCharCap)
        {
            result = result[..settings.SsisDefinitionCharCap] +
                     $"\n\n[Truncated at {settings.SsisDefinitionCharCap:N0} chars.]";
        }
        return result;
    }

    [McpServerTool, Description("Extracts all inline SQL statements from an SSIS package — from Execute SQL tasks, OLE DB sources/destinations, and other components.")]
    public async Task<string> get_ssis_sql_statements(
        [Description("Package name (e.g., 'MyPackage.dtsx').")] string package_name,
        [Description("Project name.")] string project_name,
        [Description("Folder name.")] string folder_name,
        [Description("Server label: 'MainServer' or 'SsisServer'. Leave empty to search both.")] string? server_label = null)
    {
        var xml = await GetPackageXml(package_name, project_name, folder_name, server_label);
        if (xml.StartsWith("Error") || xml.StartsWith("No ") || xml.StartsWith("Server"))
            return xml;

        var doc = XDocument.Parse(xml);
        var sb = new StringBuilder();
        sb.AppendLine($"=== SQL Statements in {package_name} ===");
        sb.AppendLine();

        int count = 0;

        // 1. Execute SQL tasks — SqlStatementSource in SqlTaskData
        foreach (var sqlTaskData in doc.Descendants(SqlTaskNs + "SqlTaskData"))
        {
            var sqlSource = sqlTaskData.Attribute(SqlTaskNs + "SqlStatementSource")?.Value;
            if (string.IsNullOrWhiteSpace(sqlSource)) continue;

            var parent = sqlTaskData.Ancestors(DtsNs + "Executable").FirstOrDefault();
            var taskName = parent is not null ? GetDtsProperty(parent, "ObjectName") : "(unknown task)";

            count++;
            sb.AppendLine($"--- [{count}] Execute SQL Task: {taskName} ---");
            sb.AppendLine(sqlSource.Trim());
            sb.AppendLine();
        }

        // 2. Data flow component SQL (SqlCommand, OpenRowset properties)
        foreach (var prop in doc.Descendants("property"))
        {
            var propName = prop.Attribute("name")?.Value;
            if (propName is not ("SqlCommand" or "OpenRowset" or "SqlCommandVariable"))
                continue;
            if (string.IsNullOrWhiteSpace(prop.Value))
                continue;
            // SqlCommandVariable contains a variable name, not SQL
            if (propName == "SqlCommandVariable") continue;

            var component = prop.Ancestors("component").FirstOrDefault();
            var compName = component?.Attribute("name")?.Value ?? "(unknown component)";

            count++;
            sb.AppendLine($"--- [{count}] Data Flow Component: {compName} ({propName}) ---");
            sb.AppendLine(prop.Value.Trim());
            sb.AppendLine();
        }

        if (count == 0)
            return $"No inline SQL statements found in package '{package_name}'.";

        var result = sb.ToString();
        if (result.Length > settings.SsisDefinitionCharCap)
        {
            result = result[..settings.SsisDefinitionCharCap] +
                     $"\n\n[Truncated at {settings.SsisDefinitionCharCap:N0} chars. Found {count} SQL statements.]";
        }
        return result;
    }

    [McpServerTool, Description("Searches the raw XML content of all SSIS packages for a keyword (table name, column, sproc, etc.). Finds which packages reference it.")]
    public async Task<string> search_ssis_package_content(
        [Description("Keyword to search for in package XML.")] string search_term,
        [Description("Folder name to limit search scope.")] string folder_name,
        [Description("Project name to limit search scope. Leave empty for all projects in the folder.")] string? project_name = null)
    {
        // Get list of projects in the folder
        var projectSql = """
            SELECT pr.name
            FROM [catalog].[projects] pr
            INNER JOIN [catalog].[folders] f ON pr.folder_id = f.folder_id
            WHERE f.name = @folder
              AND (@project IS NULL OR pr.name = @project)
            ORDER BY pr.name
            """;

        var sb = new StringBuilder();
        sb.AppendLine($"Packages containing '{search_term}':");
        sb.AppendLine();
        int total = 0;

        foreach (var (label, conn) in connectionFactory.CreateSsisConnections())
        {
            try
            {
                await conn.OpenAsync();

                // First get the project names
                var projectNames = new List<string>();
                await using (var cmd = connectionFactory.CreateCommand(projectSql, conn))
                {
                    cmd.Parameters.AddWithValue("@folder", folder_name);
                    cmd.Parameters.AddWithValue("@project", (object?)project_name ?? DBNull.Value);
                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                        projectNames.Add(reader.GetString(0));
                }

                // For each project, get the zip and search packages
                foreach (var projName in projectNames)
                {
                    await using var cmd = connectionFactory.CreateCommand(
                        "EXEC [catalog].[get_project] @folder_name = @folder, @project_name = @proj", conn);
                    cmd.Parameters.AddWithValue("@folder", folder_name);
                    cmd.Parameters.AddWithValue("@proj", projName);

                    await using var reader = await cmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync() && !reader.IsDBNull(0))
                    {
                        var zipBytes = (byte[])reader.GetValue(0);
                        using var zipStream = new MemoryStream(zipBytes);
                        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

                        foreach (var entry in archive.Entries.Where(e => e.Name.EndsWith(".dtsx", StringComparison.OrdinalIgnoreCase)))
                        {
                            using var entryStream = entry.Open();
                            using var sr = new StreamReader(entryStream);
                            var xmlContent = await sr.ReadToEndAsync();

                            if (xmlContent.Contains(search_term, StringComparison.OrdinalIgnoreCase))
                            {
                                total++;
                                int occurrences = 0;
                                int idx = 0;
                                while ((idx = xmlContent.IndexOf(search_term, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
                                {
                                    occurrences++;
                                    idx += search_term.Length;
                                }

                                sb.AppendLine($"  [{label}] {projName}/{entry.Name} ({occurrences} occurrence(s))");

                                if (total >= settings.ListCap)
                                {
                                    sb.AppendLine($"  [Capped at {settings.ListCap} results]");
                                    return sb.ToString();
                                }
                            }
                        }
                    }
                }
            }
            catch (SqlException ex) when (ex.Number == 229 || ex.Number == 230)
            {
                sb.AppendLine($"  [{label}] Access denied. Package XML search requires 'ssis_admin' or 'db_owner' role on SSISDB. (SQL {ex.Number}: {ex.Message})");
            }
            catch (SqlException ex) when (ex.Number == 208 || ex.Number == 4060)
            {
                // SSISDB does not exist on this server — skip silently
            }
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
            return $"No SSIS packages containing '{search_term}' found.";

        return sb.ToString();
    }

    #region Helpers

    private async Task<string> GetPackageXml(string packageName, string projectName, string folderName, string? serverLabel)
    {
        foreach (var (label, conn) in connectionFactory.CreateSsisConnections())
        {
            if (serverLabel is not null && !string.Equals(label, serverLabel, StringComparison.OrdinalIgnoreCase))
            {
                await conn.DisposeAsync();
                continue;
            }

            try
            {
                await conn.OpenAsync();
                await using var cmd = connectionFactory.CreateCommand(
                    "EXEC [catalog].[get_project] @folder_name = @folder, @project_name = @project", conn);
                cmd.Parameters.AddWithValue("@folder", folderName);
                cmd.Parameters.AddWithValue("@project", projectName);

                await using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync() && !reader.IsDBNull(0))
                {
                    var zipBytes = (byte[])reader.GetValue(0);
                    using var zipStream = new MemoryStream(zipBytes);
                    using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

                    var entry = archive.Entries.FirstOrDefault(e =>
                        string.Equals(e.Name, packageName, StringComparison.OrdinalIgnoreCase));

                    if (entry is not null)
                    {
                        using var entryStream = entry.Open();
                        using var sr = new StreamReader(entryStream);
                        return await sr.ReadToEndAsync();
                    }
                }
            }
            catch (SqlException ex) when (ex.Number == 229 || ex.Number == 230)
            {
                return $"[{label}] Access denied. Package XML requires 'ssis_admin' or 'db_owner' role on SSISDB. (SQL {ex.Number}: {ex.Message})";
            }
            catch (SqlException ex) when (ex.Number == 208 || ex.Number == 4060)
            {
                // SSISDB does not exist on this server — skip silently
            }
            catch (SqlException ex)
            {
                return $"Error querying [{label}]: {ex.Message}";
            }
            finally
            {
                await conn.DisposeAsync();
            }
        }

        return $"No package '{packageName}' found in project '{projectName}' folder '{folderName}'.";
    }

    private void FormatExecutable(XElement exe, StringBuilder sb, int indent)
    {
        var prefix = new string(' ', indent * 2);
        var name = GetDtsProperty(exe, "ObjectName") ?? "(unnamed)";
        var creationName = GetDtsProperty(exe, "CreationName") ?? "";
        var taskType = ClassifyTask(creationName);

        sb.AppendLine($"{prefix}[{taskType}] {name}");

        // For Execute SQL tasks, show the SQL
        var sqlTaskData = exe.Descendants(SqlTaskNs + "SqlTaskData").FirstOrDefault();
        if (sqlTaskData is not null)
        {
            var sqlSource = sqlTaskData.Attribute(SqlTaskNs + "SqlStatementSource")?.Value;
            if (!string.IsNullOrWhiteSpace(sqlSource))
            {
                var preview = sqlSource.Trim().ReplaceLineEndings(" ");
                if (preview.Length > 200) preview = preview[..200] + "...";
                sb.AppendLine($"{prefix}  SQL: {preview}");
            }
        }

        // Recurse into nested executables (containers, sequence containers, for loops, etc.)
        var nested = exe.Element(DtsNs + "Executables")?.Elements(DtsNs + "Executable").ToList() ?? [];
        foreach (var child in nested)
        {
            FormatExecutable(child, sb, indent + 1);
        }
    }

    private static XElement? FindExecutableByName(XElement root, string name)
    {
        foreach (var exe in root.Descendants(DtsNs + "Executable"))
        {
            if (string.Equals(GetDtsProperty(exe, "ObjectName"), name, StringComparison.OrdinalIgnoreCase))
                return exe;
        }
        return null;
    }

    private static string? GetDtsProperty(XElement element, string propertyName)
    {
        // Try attribute first (DTS:ObjectName style)
        var attr = element.Attribute(DtsNs + propertyName);
        if (attr is not null) return attr.Value;

        // Try child property element
        return element.Elements(DtsNs + "Property")
            .FirstOrDefault(p => p.Attribute(DtsNs + "Name")?.Value == propertyName)?.Value;
    }

    private static string ClassifyTask(string creationName) => creationName switch
    {
        _ when creationName.Contains("ExecuteSQLTask", StringComparison.OrdinalIgnoreCase) => "Execute SQL",
        _ when creationName.Contains("Pipeline", StringComparison.OrdinalIgnoreCase) => "Data Flow",
        _ when creationName.Contains("ScriptTask", StringComparison.OrdinalIgnoreCase) => "Script Task",
        _ when creationName.Contains("SendMailTask", StringComparison.OrdinalIgnoreCase) => "Send Mail",
        _ when creationName.Contains("FileSystemTask", StringComparison.OrdinalIgnoreCase) => "File System",
        _ when creationName.Contains("FTPTask", StringComparison.OrdinalIgnoreCase) => "FTP",
        _ when creationName.Contains("ExecuteProcess", StringComparison.OrdinalIgnoreCase) => "Execute Process",
        _ when creationName.Contains("ExecutePackage", StringComparison.OrdinalIgnoreCase) => "Execute Package",
        _ when creationName.Contains("Sequence", StringComparison.OrdinalIgnoreCase) => "Sequence Container",
        _ when creationName.Contains("ForLoop", StringComparison.OrdinalIgnoreCase) => "For Loop",
        _ when creationName.Contains("ForEachLoop", StringComparison.OrdinalIgnoreCase) => "ForEach Loop",
        _ when creationName.Contains("Expression", StringComparison.OrdinalIgnoreCase) => "Expression",
        _ when string.IsNullOrEmpty(creationName) => "Task",
        _ => creationName
    };

    private static string categorizeComponent(string contactInfo, string classId)
    {
        var combined = $"{contactInfo} {classId}";
        return combined switch
        {
            _ when combined.Contains("Source", StringComparison.OrdinalIgnoreCase) => "Source",
            _ when combined.Contains("Destination", StringComparison.OrdinalIgnoreCase) => "Destination",
            _ when combined.Contains("Lookup", StringComparison.OrdinalIgnoreCase) => "Lookup",
            _ when combined.Contains("DerivedColumn", StringComparison.OrdinalIgnoreCase) => "Derived Column",
            _ when combined.Contains("ConditionalSplit", StringComparison.OrdinalIgnoreCase) => "Conditional Split",
            _ when combined.Contains("Multicast", StringComparison.OrdinalIgnoreCase) => "Multicast",
            _ when combined.Contains("UnionAll", StringComparison.OrdinalIgnoreCase) => "Union All",
            _ when combined.Contains("Sort", StringComparison.OrdinalIgnoreCase) => "Sort",
            _ when combined.Contains("Aggregate", StringComparison.OrdinalIgnoreCase) => "Aggregate",
            _ when combined.Contains("Merge", StringComparison.OrdinalIgnoreCase) => "Merge",
            _ when combined.Contains("RowCount", StringComparison.OrdinalIgnoreCase) => "Row Count",
            _ when combined.Contains("DataConversion", StringComparison.OrdinalIgnoreCase) => "Data Conversion",
            _ when combined.Contains("Script", StringComparison.OrdinalIgnoreCase) => "Script Component",
            _ => "Transform"
        };
    }

    private static string MaskConnectionString(string connStr)
    {
        // Mask passwords in connection strings
        var builder = new StringBuilder(connStr);
        var lower = connStr.ToLowerInvariant();
        var pwdIdx = lower.IndexOf("password=");
        if (pwdIdx >= 0)
        {
            var endIdx = connStr.IndexOf(';', pwdIdx);
            if (endIdx < 0) endIdx = connStr.Length;
            builder.Remove(pwdIdx, endIdx - pwdIdx);
            builder.Insert(pwdIdx, "Password=****");
        }
        return builder.ToString();
    }

    private static string ExtractTaskName(string? refId)
    {
        if (refId is null) return "?";
        // RefIds look like "Package\TaskName" — extract last segment
        var lastSlash = refId.LastIndexOf('\\');
        return lastSlash >= 0 ? refId[(lastSlash + 1)..] : refId;
    }

    #endregion
}
