namespace SQL_MCP;

public class ServerSettings
{
    /// <summary>Maximum rows returned by list tools before truncation.</summary>
    public int ListCap { get; init; } = 100;

    /// <summary>Maximum characters returned from a definition body before truncation.</summary>
    public int DefinitionCharCap { get; init; } = 8_000;

    /// <summary>Lines of surrounding context returned per keyword match in definition filter mode.</summary>
    public int ContextLines { get; init; } = 15;

    /// <summary>SQL command timeout in seconds applied to all queries.</summary>
    public int CommandTimeout { get; init; } = 30;
}
