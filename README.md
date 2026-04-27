# SQL MCP Server

A [Model Context Protocol (MCP)](https://modelcontextprotocol.io) server that gives AI agents structured, read-only access to a SQL Server database. Built on .NET 8 using the `stdio` transport, it is compatible with any MCP-capable client — Claude Desktop, GitHub Copilot Agent Mode, the MCP Inspector, or a custom host.

---

## Features

- **15 tools** covering discovery, schema inspection, code search, and safe data sampling
- **Multi-catalog support** — every tool accepts an optional `catalog` parameter to query any accessible database without reconfiguring
- **Bounded responses** — list tools cap at a configurable row limit with total-count notices; definition tools truncate at a configurable character limit
- **Keyword context extraction** — definition and trigger tools accept a `filter` parameter that returns only the ±N lines surrounding each match instead of the full body
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
    "SqlServer": "Server=YOUR_SERVER;Database=YOUR_DATABASE;Trusted_Connection=True;TrustServerCertificate=True;"
  },
  "ServerSettings": {
    "ListCap": 100,
    "DefinitionCharCap": 8000,
    "ContextLines": 15,
    "CommandTimeout": 30
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

The server communicates over **stdio** (standard input/output). It cannot be tested by typing into a terminal directly — it requires an MCP client.

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

### GitHub Copilot (VS Code Agent Mode)

Add to VS Code `settings.json` (`Ctrl+Shift+P` ? *Open User Settings JSON*):

```json
"mcp": {
  "servers": {
    "sql-mcp": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "C:\\path\\to\\SQL_MCP\\SQL_MCP.csproj"]
    }
  }
}
```

Switch Copilot Chat to **Agent mode** to use the tools.

### MCP Inspector (browser UI for testing)

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
??? Program.cs                  — Host setup, DI registration, discovery arg handling
??? ServerSettings.cs           — Strongly-typed configuration POCO
??? SqlConnectionFactory.cs     — Connection and command creation with timeout
??? ToolHelpers.cs              — Shared formatting utilities (cap notices, definition processing, error formatting)
??? ManifestService.cs          — --info / --list-tools flag handler
??? appsettings.json            — Local config (gitignored)
??? appsettings.example.json    — Committed template for new contributors
??? mcp-manifest.json           — Static machine-readable server descriptor
??? AGENT_TESTING.md            — Prompt-ready guide for agent sessions
??? SQL_MCP.csproj
??? Tools/
    ??? NavigationTools.cs      — list_catalogs, list_schemas, list_tables, list_objects
    ??? DefinitionTools.cs      — get_object_definition
    ??? SchemaTools.cs          — get_table_schema, get_table_dependencies, search_columns, get_indexes, get_triggers
    ??? SprocTools.cs           — search_database_code, get_sproc_parameters
    ??? DataTools.cs            — get_data_sample, run_query, get_row_counts
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
- The server has no authentication layer of its own — access control is delegated entirely to SQL Server credentials in the connection string.
- For production use, prefer a service account with the minimum required permissions (`db_datareader` on target databases).
