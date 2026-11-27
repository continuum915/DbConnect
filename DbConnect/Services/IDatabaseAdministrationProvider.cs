using System;
using System.Threading;
using System.Threading.Tasks;

namespace DbConnect.Services;

/// <summary>Database administration operations, independent of other provider roles.</summary>
public interface IDatabaseAdministrationProvider
{
    Task CreateUserAsync(string connectionString, string username, string password, CancellationToken ct = default) =>
        throw new System.NotSupportedException("This provider does not support user management.");

    Task DropUserAsync(string connectionString, string username, CancellationToken ct = default) =>
        throw new System.NotSupportedException("This provider does not support user management.");

    Task ChangePasswordAsync(string connectionString, string username, string newPassword, CancellationToken ct = default) =>
        throw new System.NotSupportedException("This provider does not support user management.");

    Task GrantPrivilegeAsync(string connectionString, string username, string privilege, string? objectName, CancellationToken ct = default) =>
        throw new System.NotSupportedException("This provider does not support user management.");

    Task RevokePrivilegeAsync(string connectionString, string username, string privilege, string? objectName, CancellationToken ct = default) =>
        throw new System.NotSupportedException("This provider does not support user management.");

    string GenerateCreateUserSql(string username, string password) => string.Empty;
    string GenerateDropUserSql(string username) => string.Empty;
    string GenerateChangePasswordSql(string username, string newPassword) => string.Empty;
    string GenerateGrantSql(string username, string privilege, string? objectName) => string.Empty;
    string GenerateRevokeSql(string username, string privilege, string? objectName) => string.Empty;
}
