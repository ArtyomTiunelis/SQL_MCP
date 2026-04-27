using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace SQL_MCP;

public class SqlConnectionFactory(IConfiguration configuration, ServerSettings settings)
{
    private readonly string _baseConnectionString =
        configuration.GetConnectionString("SqlServer")
        ?? throw new InvalidOperationException("Connection string 'SqlServer' is not configured.");

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
}
