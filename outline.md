# SQL Server MCP Tool Specifications

This document outlines the standard Model Context Protocol (MCP) tool definitions required for the custom SQL Server assistant. These specifications dictate how the AI client will see and interact with your server.

1. `get_sproc_definition`

Description: Retrieves the full T-SQL script definition of a specific stored procedure. Use this tool when you need to understand the exact internal logic, input parameters, or output result sets of a procedure called by the application code.

Input Schema:
```json
{
  "type": "object",
  "properties": {
    "sproc_name": {
      "type": "string",
      "description": "The exact name of the stored procedure (e.g., 'sp_GetUserData' or 'dbo.sp_ProcessOrder')."
    }
  },
  "required": ["sproc_name"]
}
```

2. `search_database_code`

Description: Searches the raw SQL text definitions of all stored procedures, views, and functions for a specific keyword, table name, or column name. Useful for tracing dependencies, finding where a specific table is updated, or locating poorly-named logic across the massive database.

Input Schema:
```json
{
  "type": "object",
  "properties": {
    "search_term": {
      "type": "string",
      "description": "The keyword, variable, or object name to search for within the database code."
    }
  },
  "required": ["search_term"]
}
```

3. `get_table_schema`

Description: Retrieves the database schema details for a specific table. Returns a structured list of column names, their specific SQL data types, maximum character lengths, and whether they allow NULL values. Use this to understand the data contract before writing queries or application models.

Input Schema:
```json
{
  "type": "object",
  "properties": {
    "table_name": {
      "type": "string",
      "description": "The exact name of the database table (e.g., 'Users' or 'dbo.Orders')."
    }
  },
  "required": ["table_name"]
}
```

4. `get_table_dependencies`

Description: Retrieves all Foreign Key relationships associated with a specific table. It returns data showing where the table acts as a parent (referenced by others) and where it acts as a child (referencing others). Crucial for understanding entity relationships and writing accurate JOIN statements without hallucinating connections.

Input Schema:
```json
{
  "type": "object",
  "properties": {
    "table_name": {
      "type": "string",
      "description": "The exact name of the database table to check for foreign key dependencies."
    }
  },
  "required": ["table_name"]
}
```

5. `get_data_sample`

Description: Retrieves a safe, highly limited sample (TOP 5 rows) of actual data from a specific table. Use this tool to inspect real-world data formatting, understand the actual values of enumerations/status codes, and verify edge cases that the schema alone does not explain.

Input Schema:
```json
{
  "type": "object",
  "properties": {
    "table_name": {
      "type": "string",
      "description": "The exact name of the database table to sample."
    }
  },
  "required": ["table_name"]
}
```