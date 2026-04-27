using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace SQL_MCP;

public class SqlConnectionFactory(IConfiguration configuration, ServerSettings settings)
{
    private readonly string _baseConnectionString =
        configuration.GetConnectionString("SqlServer")
        ?? throw new InvalidOperationException("Connection string 'SqlServer' is not configured.");

    private readonly string? _ssisConnectionString =
        configuration.GetConnectionString("SsisServer");

    public SqlConnection CreateConnection(string? catalog = null)
    {
        if (catalog is null)
            return new SqlConnection(_baseConnectionString);

        var builder = new SqlConnectionStringBuilder(_baseConnectionString)
        {
            InitialCatalog = catalog
        };
        return new SqlConnection(builder.ConnectionString);
    }

    /// <summary>
    /// Creates a command with the configured timeout applied.
    /// </summary>
    public SqlCommand CreateCommand(string sql, SqlConnection connection) =>
        new(sql, connection) { CommandTimeout = settings.CommandTimeout };

    /// <summary>
    /// Returns labeled connections to SSISDB on all configured servers.
    /// The main SQL server is included (pointing at SSISDB) plus the dedicated SSIS server if configured.
    /// </summary>
    public IEnumerable<(string Label, SqlConnection Connection)> CreateSsisConnections()
    {
        // Main server — point at SSISDB
        var mainBuilder = new SqlConnectionStringBuilder(_baseConnectionString)
        {
            InitialCatalog = "SSISDB"
        };
        yield return ("MainServer", new SqlConnection(mainBuilder.ConnectionString));

        // Dedicated SSIS server
        if (_ssisConnectionString is not null)
        {
            yield return ("SsisServer", new SqlConnection(_ssisConnectionString));
        }
    }
}
