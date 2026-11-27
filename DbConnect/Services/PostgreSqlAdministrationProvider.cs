using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace DbConnect.Services;

internal sealed class PostgreSqlAdministrationProvider : IDatabaseAdministrationProvider
{
    public async Task CreateUserAsync(string connectionString, string username, string password, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"CREATE USER {QuoteIdentifier(username)} WITH PASSWORD '{EscapeString(password)}'";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DropUserAsync(string connectionString, string username, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"DROP USER IF EXISTS {QuoteIdentifier(username)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ChangePasswordAsync(string connectionString, string username, string newPassword, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER USER {QuoteIdentifier(username)} WITH PASSWORD '{EscapeString(newPassword)}'";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task GrantPrivilegeAsync(string connectionString, string username, string privilege, string? objectName, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = string.IsNullOrWhiteSpace(objectName)
            ? $"GRANT {privilege} TO {QuoteIdentifier(username)}"
            : $"GRANT {privilege} ON {objectName} TO {QuoteIdentifier(username)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RevokePrivilegeAsync(string connectionString, string username, string privilege, string? objectName, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = string.IsNullOrWhiteSpace(objectName)
            ? $"REVOKE {privilege} FROM {QuoteIdentifier(username)}"
            : $"REVOKE {privilege} ON {objectName} FROM {QuoteIdentifier(username)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public string GenerateCreateUserSql(string username, string password) =>
        $"CREATE USER {QuoteIdentifier(username)} WITH PASSWORD '{EscapeString(password)}';";

    public string GenerateDropUserSql(string username) =>
        $"DROP USER IF EXISTS {QuoteIdentifier(username)};";

    public string GenerateChangePasswordSql(string username, string newPassword) =>
        $"ALTER USER {QuoteIdentifier(username)} WITH PASSWORD '{EscapeString(newPassword)}';";

    public string GenerateGrantSql(string username, string privilege, string? objectName) =>
        string.IsNullOrWhiteSpace(objectName)
            ? $"GRANT {privilege} TO {QuoteIdentifier(username)};"
            : $"GRANT {privilege} ON {objectName} TO {QuoteIdentifier(username)};";

    public string GenerateRevokeSql(string username, string privilege, string? objectName) =>
        string.IsNullOrWhiteSpace(objectName)
            ? $"REVOKE {privilege} FROM {QuoteIdentifier(username)};"
            : $"REVOKE {privilege} ON {objectName} FROM {QuoteIdentifier(username)};";

    private string QuoteIdentifier(string identifier) => ProviderCapabilityDeclarations.PostgreSql.QuoteIdentifier(identifier);

    private static string EscapeString(string value) => value.Replace("'", "''");
}
