# SQL MCP Server — Agent Testing Guide

You have access to a SQL Server MCP tool server. It gives you structured, read-only access to a SQL Server database. Use the tools below to explore the database before writing any queries or application code.

## Starting the Server

```powershell
dotnet run --project C:\Users\ATIUNELIS\Source\repos\SQL_MCP\SQL_MCP.csproj
```

To discover available tools without starting the full server:

```powershell
dotnet run --project C:\Users\ATIUNELIS\Source\repos\SQL_MCP\SQL_MCP.csproj -- --list-tools
dotnet run --project C:\Users\ATIUNELIS\Source\repos\SQL_MCP\SQL_MCP.csproj -- --info
```

---

## Available Tools

### Discovery & Navigation

#### `list_schemas`
Lists all user-defined schemas in the database. **Always call this first** on an unfamiliar database.
```
(no parameters)
```

#### `list_tables`
Lists all tables, optionally filtered by schema. Never guess table names — enumerate them.
```
schema: "dbo"         ? optional
```

#### `list_objects`
Lists stored procedures, views, or functions by schema and/or type.
```
schema: "dbo"         ? optional
type: "procedure"     ? optional: "procedure", "view", "function"
```

---

### Definitions

#### `get_sproc_definition`
Returns the full T-SQL body of a stored procedure. Always read the actual definition before assuming what it does.
```
sproc_name: "dbo.sp_GetUserData"
```

#### `get_sproc_parameters`
Returns just the parameter signature of a procedure — names, types, direction, defaults.
Use this when you only need to know how to call it, not what it does internally.
```
sproc_name: "dbo.sp_GetUserData"
```

#### `get_view_definition`
Returns the full T-SQL body of a view.
```
view_name: "dbo.vw_ActiveOrders"
```

#### `get_function_definition`
Returns the full T-SQL body of a scalar or table-valued function.
```
function_name: "dbo.fn_GetDiscount"
```

#### `search_database_code`
Searches across all stored procedures, views, and functions for a keyword.
Use this to find where a table is written to, where a column is used, or where specific logic lives.
```
search_term: "OrderStatus"
```

---

### Schema Intelligence

#### `get_table_schema`
Returns all columns for a table: name, data type, max length, and nullability.
Always call this before writing INSERT, UPDATE, or SELECT statements against an unfamiliar table.
```
table_name: "dbo.Orders"
```

#### `get_table_dependencies`
Returns all foreign key relationships for a table — both as parent and child.
Use this to understand how to JOIN tables correctly without guessing.
```
table_name: "dbo.Orders"
```

#### `search_columns`
Finds all tables containing a column matching a name or partial name.
Use this to locate where a specific piece of data lives across the entire database.
```
column_name: "StatusID"
```

#### `get_indexes`
Returns all indexes on a table: columns, uniqueness, clustering.
Call this before querying large tables to understand available access paths.
```
table_name: "dbo.Orders"
```

#### `get_triggers`
Returns all triggers on a table with their full T-SQL body.
Triggers often contain hidden business logic not visible in stored procedures — always check.
```
table_name: "dbo.Orders"
```

---

### Data & Runtime

#### `get_row_counts`
Returns approximate row counts for all tables. Call this before querying to understand data volume.
```
schema: "dbo"         ? optional
```

#### `get_data_sample`
Returns the TOP 5 rows from a table. Use this to understand real-world data formats and enum values.
```
table_name: "dbo.Orders"
```

#### `run_query`
Executes a read-only SELECT statement. Results are capped at 200 rows. All write and DDL statements are rejected.
```
query: "SELECT TOP 20 * FROM dbo.Orders WHERE StatusID = 3"
```

---

## Recommended Exploration Workflow

When starting fresh on an unfamiliar database:

1. `list_schemas` ? understand how the database is partitioned
2. `list_tables` (per schema) ? enumerate all tables without guessing
3. `get_row_counts` ? identify high-volume tables before querying them
4. `get_table_schema` ? understand column structure and types
5. `get_table_dependencies` ? understand FK relationships for correct JOINs
6. `search_columns` ? locate a concept (e.g. `StatusID`) across all tables
7. `get_indexes` ? understand access paths before writing queries on large tables
8. `get_triggers` ? check for hidden write logic
9. `get_data_sample` or `run_query` ? inspect real data values
10. `list_objects` ? discover what procedures/views/functions exist
11. `search_database_code` ? find all objects referencing a table or column
12. `get_sproc_parameters` ? check how to call a procedure
13. `get_sproc_definition` / `get_view_definition` / `get_function_definition` ? read the full logic
