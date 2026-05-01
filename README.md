# SQL MCP Server

A [Model Context Protocol (MCP)](https://modelcontextprotocol.io) server that gives AI agents structured, read-only access to a SQL Server database. Built on .NET 8 using the `stdio` transport, it is compatible with any MCP-capable client — Claude Desktop, Visual Studio, Visual Studio Code, GitHub Copilot Agent Mode, the MCP Inspector, or a custom host.

---

## Features

- **26 tools** covering discovery, schema inspection, code search, safe data sampling, and SSIS package exploration
- **Multi-catalog support** — every tool accepts an optional `catalog` parameter to query any accessible database without reconfiguring
- **Bounded responses** — list tools cap at a configurable row limit with total-count notices; definition tools truncate at a configurable character limit
- **Keyword context extraction** — definition and trigger tools accept a `filter` parameter that returns only the ±N lines surrounding each match instead of the full body
- **SSIS catalog integration** — browse folders, projects, and packages across multiple servers; parse package XML to extract control flow, data flow details, and inline SQL
- **Read-only enforcement** — `run_query` rejects any statement that does not start with `SELECT`
- **Structured error handling** — SQL exceptions are caught and returned as clear, agent-readable messages including SQL error number and state
- **Discoverability flags** — `--info` and `--list-tools` print the server manifest without starting the MCP host

---

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Access to a SQL Server instance (SQL Server 2016+ or Azure SQL)
- Appropriate read permissions on target databases

---

## Getting Started

### 1. Clone and configure

```powershell
git clone <repo-url>
cd SQL_MCP
copy appsettings.example.json appsettings.json
```

Edit `appsettings.json` with your connection details:

```json
{
  "ConnectionStrings": {
    "SqlServer": "Server=YOUR_SERVER;Database=YOUR_DATABASE;Trusted_Connection=True;TrustServerCertificate=True;",
    "SsisServer": "Server=YOUR_SSIS_SERVER;Database=SSISDB;Trusted_Connection=True;TrustServerCertificate=True;"
  },
  "ServerSettings": {
    "ListCap": 100,
    "DefinitionCharCap": 8000,
    "ContextLines": 15,
    "CommandTimeout": 30,
    "SsisDefinitionCharCap": 12000,
    "SsisExecutionHistoryLimit": 20
  }
}
```

### 2. Build

```powershell
dotnet build
```

### 3. Verify discoverability

```powershell
# Full server manifest
dotnet run -- --info

# Tools list only
dotnet run -- --list-tools
```

---

## Connecting to an MCP Client

The server communicates over **stdio** (standard input/output). It cannot be tested by typing into a terminal directly — it requires an MCP client. For IDEs, the cleanest setup is to register the server in an `mcp.json` file.

### Claude Desktop

Add to `%APPDATA%\Claude\claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "sql-mcp": {
      "command": "dotnet",
      "args": ["run", "--project", "C:\\path\\to\\SQL_MCP\\SQL_MCP.csproj"]
    }
  }
}
```

Restart Claude Desktop. The tools will appear automatically in the tool panel.

### GitHub Copilot in Visual Studio 2022

Visual Studio natively supports MCP servers through GitHub Copilot Agent mode. You simply need to create or edit an `.mcp.json` file in the appropriate location.

User-wide setup:

- `%USERPROFILE%\.mcp.json`

To keep the server available across all your Visual Studio projects:

1. Open File Explorer.
2. Paste `%USERPROFILE%` into the address bar and press Enter.
3. Open or create a file named `.mcp.json` in that folder.
4. Add the server entry below and save the file.
5. Visual Studio will automatically detect the changes and reload the agent (no restart required).

Workspace-specific setup:

- `.mcp.json` (in the repository root)

To keep the server scoped to one repository (and optionally commit it to source control for the team):

1. Open the repository in Visual Studio.
2. In the Solution Explorer, right-click the root folder or solution and select **Add > New Item**.
3. Create a file named `.mcp.json` in the root directory.
4. Add the same server entry below and save the file.
5. Visual Studio will automatically detect the changes and reload the agent.

Use the same server definition in either location:

```json
{
  "servers": {
    "sql-mcp": {
      "type": "stdio",
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "C:\\path\\to\\SQL_MCP\\SQL_MCP.csproj"
      ]
    }
  }
}
``` 

### GitHub Copilot in VS Code

Create or edit the VS Code MCP config file instead of putting the server entry in `settings.json`.

User-wide setup:

- `%APPDATA%\Code\User\mcp.json`

To find the user profile config in VS Code:

1. Open File Explorer.
2. Paste `%APPDATA%\Code\User` into the address bar.
3. Open or create `mcp.json` in that folder.
4. Add the server entry below and save the file.
5. Restart VS Code.

Workspace-specific setup:

- `.vscode/mcp.json`

To keep the server scoped to one repository:

1. Open the repository in VS Code.
2. Create a `.vscode` folder if it does not already exist.
3. Create or edit `.vscode/mcp.json`.
4. Add the same server entry below and save the file.
5. Restart VS Code.

Use the same server definition in either location:

```json
{
  "servers": {
    "sql-mcp": {
      "type": "stdio",
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "C:\\path\\to\\SQL_MCP\\SQL_MCP.csproj"
      ]
    }
  },
  "inputs": []
}
```

After saving the file, restart VS Code and switch Copilot Chat to **Agent mode** to use the tools.

Requires Node.js:

```powershell
npx @modelcontextprotocol/inspector dotnet run --project C:\path\to\SQL_MCP\SQL_MCP.csproj
```

Opens a local browser UI where you can call each tool with form inputs.

---

## Configuration Reference

All values are in the `ServerSettings` section of `appsettings.json`. Defaults apply if the section is absent.

| Setting | Default | Description |
|---|---|---|
| `ListCap` | `100` | Maximum rows returned by list and search tools. When hit, a notice shows the total match count and suggests narrowing filters. |
| `DefinitionCharCap` | `8000` | Maximum characters returned from a definition body. Truncated definitions include a notice with the full character count and instructions to use the `filter` parameter. |
| `ContextLines` | `15` | Lines of surrounding context returned per keyword match when using the `filter` parameter on definition tools. |
| `CommandTimeout` | `30` | SQL command timeout in seconds applied to all queries. |
| `SsisDefinitionCharCap` | `12000` | Maximum characters returned from an SSIS package definition summary before truncation. |
| `SsisExecutionHistoryLimit` | `20` | Maximum number of recent SSIS executions returned by `get_ssis_execution_history`. |

---

## Tools Reference

### Discovery & Navigation

| Tool | Description |
|---|---|
| `list_catalogs` | Lists all accessible SQL Server databases. Call this first when targeting an unfamiliar server. |
| `list_schemas` | Lists all user-defined schemas in a database. Call before `list_tables` to understand partitioning. |
| `list_tables` | Lists all user tables, filterable by schema and name substring. |
| `list_objects` | Lists stored procedures, views, and functions, filterable by schema, type, and name substring. |

### Definitions

| Tool | Description |
|---|---|
| `get_object_definition` | Returns the full T-SQL body of a stored procedure, view, or function. Accepts a `filter` keyword to return only surrounding context lines instead of the full body. |
| `search_database_code` | Searches all object definitions for a keyword. Returns matching object names and types. |

### Schema Intelligence

| Tool | Description |
|---|---|
| `get_table_schema` | Returns all columns for a table: name, data type, max length, and nullability. |
| `get_table_dependencies` | Returns all foreign key relationships for a table — both as parent and child. |
| `search_columns` | Finds all tables containing a column matching a name or partial name. |
| `get_indexes` | Returns all indexes on a table with key columns, uniqueness, and clustering type. |
| `get_triggers` | Returns all triggers on a table with their full T-SQL body. Accepts a `filter` keyword for context extraction. |
| `get_sproc_parameters` | Returns the parameter signature of a stored procedure without fetching the full body. |

### Data & Runtime

| Tool | Description |
|---|---|
| `get_row_counts` | Returns approximate row counts for all tables using SQL Server statistics, sorted by volume descending. |
| `get_data_sample` | Returns the TOP 5 rows from a table. |
| `run_query` | Executes a read-only `SELECT` statement. Results are capped at 200 rows. All write and DDL statements are rejected. |

### SSIS Catalog

| Tool | Description |
|---|---|
| `list_ssis_folders` | Lists all SSIS catalog folders across all configured servers. |
| `list_ssis_projects` | Lists projects in a catalog folder. |
| `list_ssis_packages` | Lists packages in a project. |
| `get_ssis_package_parameters` | Returns parameters for an SSIS package. |
| `search_ssis_packages` | Searches package names across all servers for a keyword. |
| `get_ssis_execution_history` | Returns recent execution history for a package. |
| `get_ssis_execution_errors` | Returns error messages for a specific execution. |

### SSIS Package Definitions

| Tool | Description |
|---|---|
| `get_ssis_package_definition` | Returns a summarized view of a package: tasks, data flows, precedence constraints, and connection managers parsed from the package XML. |
| `get_ssis_dataflow_details` | Returns details of a Data Flow task: source/destination components, transformations, SQL commands, and column mappings. |
| `get_ssis_sql_statements` | Extracts all inline SQL from Execute SQL tasks and Data Flow components in a package. |
| `search_ssis_package_content` | Searches the raw XML of all packages in a folder/project for a keyword to find which packages reference a table, column, or sproc. |

---

### Multi-Catalog Usage

Every tool except `list_catalogs` accepts an optional `catalog` parameter. When provided, the connection is transparently re-pointed to that database while all other connection string settings (server, auth, TLS) remain unchanged.

```
list_tables     schema:"dbo"              catalog:"Reporting"
get_table_schema  table_name:"dbo.Orders"  catalog:"Archive"
run_query       query:"SELECT TOP 10 ..."  catalog:"Nova"
```

---

### Bounded Response Behaviour

**List tools** (`list_tables`, `list_objects`, `search_database_code`, `search_columns`, `get_row_counts`) cap at `ListCap` rows and always append a count notice:

```
[Showing 100 of 312 — use the filter parameter to narrow: filter:"Invoice"]
```

**Definition tools** (`get_object_definition`, `get_triggers`) apply two modes:

- **Without `filter`**: full body returned up to `DefinitionCharCap` characters, then truncated with a notice
- **With `filter`**: only blocks of ±`ContextLines` lines surrounding each keyword match are returned

```
-- Lines matching 'InvoiceID' in 'dbo.napProcessOrder' (2 block(s), ±15 lines of context):
-- [Lines 47–77]
... targeted lines ...
-- [Lines 203–233]
... targeted lines ...
```

---

## Project Structure

```
SQL_MCP/
├── Program.cs                  — Host setup, DI registration, discovery arg handling
├── ServerSettings.cs           — Strongly-typed configuration POCO
├── SqlConnectionFactory.cs     — Connection and command creation with timeout
├── ToolHelpers.cs              — Shared formatting utilities (cap notices, definition processing, error formatting)
├── ManifestService.cs          — --info / --list-tools flag handler
├── appsettings.json            — Local config (gitignored)
├── appsettings.example.json    — Committed template for new contributors
├── mcp-manifest.json           — Static machine-readable server descriptor
├── SQL_MCP.csproj
└── Tools/
    ├── NavigationTools.cs      — list_catalogs, list_schemas, list_tables, list_objects
    ├── DefinitionTools.cs      — get_object_definition
    ├── SchemaTools.cs          — get_table_schema, get_table_dependencies, search_columns, get_indexes, get_triggers
    ├── SprocTools.cs           — search_database_code, get_sproc_parameters
    ├── DataTools.cs            — get_data_sample, run_query, get_row_counts
    ├── SsisTools.cs            — list_ssis_folders, list_ssis_projects, list_ssis_packages, get_ssis_package_parameters, search_ssis_packages, get_ssis_execution_history, get_ssis_execution_errors
    └── SsisPackageDefinitionTools.cs — get_ssis_package_definition, get_ssis_dataflow_details, get_ssis_sql_statements, search_ssis_package_content
```

---

## Dependencies

| Package | Version | Purpose |
|---|---|---|
| `ModelContextProtocol` | 0.2.0-preview.3 | MCP server host, tool registration, stdio transport |
| `Microsoft.Data.SqlClient` | 5.2.2 | SQL Server connectivity |
| `Microsoft.Extensions.Hosting` | 8.0.1 | Generic host, dependency injection |
| `Microsoft.Extensions.Configuration.Json` | 8.0.1 | `appsettings.json` loading |

---

## Security Notes

- `appsettings.json` is excluded from source control via `.gitignore`. Use `appsettings.example.json` as a template.
- `run_query` enforces read-only access by rejecting any statement that does not begin with `SELECT` and executing within a `READ UNCOMMITTED` transaction.
- SSIS tools are read-only — no package execution or modification is possible.
- SSIS package XML parsing tools require `ssis_admin` role on SSISDB. Catalog metadata tools work with standard read permissions.
- Connection strings in SSIS connection managers are displayed with passwords masked.
- The server has no authentication layer of its own — access control is delegated entirely to SQL Server credentials in the connection string.
- For production use, prefer a service account with the minimum required permissions (`db_datareader` on target databases, `ssis_admin` on SSISDB).
