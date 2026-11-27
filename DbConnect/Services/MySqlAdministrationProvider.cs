using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;

namespace DbConnect.Services;

internal sealed class MySqlAdministrationProvider : IDatabaseAdministrationProvider
{
    public async Task CreateUserAsync(string connectionString, string username, string password, CancellationToken ct = default)
    {
        await using var connection = MySqlConnections.Create(connectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"CREATE USER {FormatAccountName(username)} IDENTIFIED BY '{EscapeString(password)}'";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DropUserAsync(string connectionString, string username, CancellationToken ct = default)
    {
        await using var connection = MySqlConnections.Create(connectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"DROP USER IF EXISTS {FormatAccountName(username)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ChangePasswordAsync(string connectionString, string username, string newPassword, CancellationToken ct = default)
    {
        await using var connection = MySqlConnections.Create(connectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER USER {FormatAccountName(username)} IDENTIFIED BY '{EscapeString(newPassword)}'";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task GrantPrivilegeAsync(string connectionString, string username, string privilege, string? objectName, CancellationToken ct = default)
    {
        await using var connection = MySqlConnections.Create(connectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"GRANT {privilege} ON {FormatPrivilegeObject(objectName)} TO {FormatAccountName(username)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RevokePrivilegeAsync(string connectionString, string username, string privilege, string? objectName, CancellationToken ct = default)
    {
        await using var connection = MySqlConnections.Create(connectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"REVOKE {privilege} ON {FormatPrivilegeObject(objectName)} FROM {FormatAccountName(username)}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public string GenerateCreateUserSql(string username, string password) =>
        $"CREATE USER {FormatAccountName(username)} IDENTIFIED BY '{EscapeString(password)}';";

    public string GenerateDropUserSql(string username) =>
        $"DROP USER IF EXISTS {FormatAccountName(username)};";

    public string GenerateChangePasswordSql(string username, string newPassword) =>
        $"ALTER USER {FormatAccountName(username)} IDENTIFIED BY '{EscapeString(newPassword)}';";

    public string GenerateGrantSql(string username, string privilege, string? objectName) =>
        $"GRANT {privilege} ON {FormatPrivilegeObject(objectName)} TO {FormatAccountName(username)};";

    public string GenerateRevokeSql(string username, string privilege, string? objectName) =>
        $"REVOKE {privilege} ON {FormatPrivilegeObject(objectName)} FROM {FormatAccountName(username)};";

    private string FormatAccountName(string username)
    {
        var trimmed = username.Trim();
        var atIndex = trimmed.LastIndexOf('@');
        if (atIndex <= 0 || atIndex == trimmed.Length - 1)
            return $"'{EscapeString(trimmed.Trim('\'', ' '))}'@'%'";

        var user = trimmed[..atIndex].Trim('\'', ' ');
        var host = trimmed[(atIndex + 1)..].Trim('\'', ' ');
        return $"'{EscapeString(user)}'@'{EscapeString(host)}'";
    }

    private string FormatPrivilegeObject(string? objectName)
    {
        if (string.IsNullOrWhiteSpace(objectName))
            return "*.*";

        var trimmed = objectName.Trim();
        if (trimmed.Contains('*') || trimmed.Contains(' ') || trimmed.Contains('(') || trimmed.Contains(')'))
            return trimmed;

        return QuoteQualifiedIdentifier(trimmed);
    }

    private static string EscapeString(string value) => value.Replace("'", "''");

    private string QuoteQualifiedIdentifier(string identifier) =>
        string.Join(".", identifier.Split('.').Select(ProviderCapabilityDeclarations.MySql.QuoteIdentifier));
}
