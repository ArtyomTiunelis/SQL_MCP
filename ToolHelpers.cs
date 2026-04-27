using Microsoft.Data.SqlClient;

namespace SQL_MCP;

internal static class ToolHelpers
{
    /// <summary>
    /// Formats a SqlException into a clear, agent-readable error string.
    /// </summary>
    internal static string FormatSqlError(SqlException ex, string operation) =>
        $"SQL error during {operation}: {ex.Message} (Number: {ex.Number}, State: {ex.State})";

    /// <summary>
    /// Appends a result count / truncation notice. Returns true if the cap was hit.
    /// </summary>
    internal static bool AppendCapNotice(System.Text.StringBuilder sb, int returned, int total, string filterHint)
    {
        if (returned < total)
        {
            sb.AppendLine($"[Showing {returned} of {total} — use the filter parameter to narrow: {filterHint}]");
            return true;
        }
        sb.AppendLine($"[{total} result(s)]");
        return false;
    }

    /// <summary>
    /// Truncates a definition body at settings.DefinitionCharCap, or extracts keyword context blocks.
    /// </summary>
    internal static string ProcessDefinition(string body, string? filter, string objectName, ServerSettings settings)
    {
        if (filter is null)
        {
            if (body.Length <= settings.DefinitionCharCap)
                return body;

            return body[..settings.DefinitionCharCap] +
                   $"\n\n[Definition truncated at {settings.DefinitionCharCap:N0} of {body.Length:N0} chars. " +
                   $"Use the filter parameter to retrieve a specific section.]";
        }

        var lines = body.Split('\n');
        var matchBlocks = new List<(int start, int end)>();

        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                int start = Math.Max(0, i - settings.ContextLines);
                int end   = Math.Min(lines.Length - 1, i + settings.ContextLines);

                if (matchBlocks.Count > 0 && matchBlocks[^1].end >= start - 1)
                    matchBlocks[^1] = (matchBlocks[^1].start, end);
                else
                    matchBlocks.Add((start, end));
            }
        }

        if (matchBlocks.Count == 0)
            return $"No lines containing '{filter}' found in '{objectName}'.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"-- Lines matching '{filter}' in '{objectName}' " +
                      $"({matchBlocks.Count} block(s), ±{settings.ContextLines} lines of context):");

        foreach (var (start, end) in matchBlocks)
        {
            sb.AppendLine($"-- [Lines {start + 1}–{end + 1}]");
            for (int i = start; i <= end; i++)
                sb.AppendLine(lines[i]);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}
