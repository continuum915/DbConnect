using MySqlConnector;

namespace DbConnect.Services;

internal static class MySqlConnections
{
    internal static MySqlConnection Create(string connectionString)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            AllowUserVariables = true
        };
        return new MySqlConnection(builder.ConnectionString);
    }
}
