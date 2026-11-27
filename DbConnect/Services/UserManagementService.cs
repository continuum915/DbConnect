using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

public class UserManagementService
{
    private static string GetValue(object?[] row, int index) =>
        index < row.Length ? row[index]?.ToString() ?? string.Empty : string.Empty;

    private static bool GetBool(object?[] row, int index)
    {
        var s = GetValue(row, index);
        return s == "True" || s == "true" || s == "1" || s == "t" || s == "YES";
    }

    private static string EscapeSqlLiteral(string value) => value.Replace("'", "''");

    private static string ToMySqlAccountName(string userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
            return string.Empty;

        var trimmed = userName.Trim();
        var atIndex = trimmed.LastIndexOf('@');
        if (atIndex <= 0 || atIndex == trimmed.Length - 1)
            return $"'{EscapeSqlLiteral(trimmed)}'@'%'";

        var user = trimmed[..atIndex].Trim('\'', ' ');
        var host = trimmed[(atIndex + 1)..].Trim('\'', ' ');
        return $"'{EscapeSqlLiteral(user)}'@'{EscapeSqlLiteral(host)}'";
    }

    public async Task<IReadOnlyList<DatabaseUser>> GetUsersAsync(DatabaseSession session)
    {
        var users = new List<DatabaseUser>();

        if (session.ProviderName == "SQLite")
        {
            users.Add(new DatabaseUser { Name = "N/A (SQLite has no user model)", IsRole = false });
            return users;
        }

        if (session.ProviderName == "PostgreSQL")
        {
            var result = await session.ExecuteQueryAsync(
                "SELECT rolname, rolcanlogin FROM pg_roles ORDER BY rolname");
            if (!result.HasError)
            {
                foreach (var row in result.ObjectRows)
                {
                    var name = GetValue(row, 0);
                    var canLogin = GetBool(row, 1);
                    users.Add(new DatabaseUser { Name = name, IsRole = !canLogin });
                }
            }

            return users;
        }

        if (session.ProviderName == "MySQL")
        {
            var result = await session.ExecuteQueryAsync(
                "SELECT CONCAT(user, '@', host), 'false' FROM mysql.user ORDER BY user, host");
            if (!result.HasError)
            {
                foreach (var row in result.ObjectRows)
                {
                    users.Add(new DatabaseUser
                    {
                        Name = GetValue(row, 0),
                        IsRole = false
                    });
                }
            }

            return users;
        }

        if (session.ProviderName == "SQL Server")
        {
            var result = await session.ExecuteQueryAsync(
                "SELECT name, type_desc FROM sys.server_principals WHERE type IN ('S','U','G','R') ORDER BY name");
            if (!result.HasError)
            {
                foreach (var row in result.ObjectRows)
                {
                    var name = GetValue(row, 0);
                    var typeDesc = GetValue(row, 1);
                    users.Add(new DatabaseUser { Name = name, IsRole = typeDesc.Contains("ROLE", StringComparison.OrdinalIgnoreCase) });
                }
            }

            return users;
        }

        return users;
    }

    public async Task<IReadOnlyList<PermissionGrant>> GetPermissionsAsync(DatabaseSession session, string userName)
    {
        var grants = new List<PermissionGrant>();

        if (session.ProviderName == "SQLite")
            return grants;

        if (session.ProviderName == "PostgreSQL")
        {
            var result = await session.ExecuteQueryAsync(
                $"SELECT table_name, privilege_type, is_grantable FROM information_schema.role_table_grants " +
                $"WHERE grantee = '{EscapeSqlLiteral(userName)}' ORDER BY table_name, privilege_type");
            if (!result.HasError)
            {
                foreach (var row in result.ObjectRows)
                {
                    grants.Add(new PermissionGrant
                    {
                        Grantee = userName,
                        ObjectType = "TABLE",
                        ObjectName = GetValue(row, 0),
                        Privilege = GetValue(row, 1),
                        WithGrantOption = GetValue(row, 2) == "YES"
                    });
                }
            }

            return grants;
        }

        if (session.ProviderName == "MySQL")
        {
            var grantee = ToMySqlAccountName(userName);
            var result = await session.ExecuteQueryAsync($@"
                SELECT '*.*', 'GLOBAL', PRIVILEGE_TYPE, IS_GRANTABLE
                FROM information_schema.USER_PRIVILEGES
                WHERE GRANTEE = '{EscapeSqlLiteral(grantee)}'
                UNION ALL
                SELECT CONCAT(TABLE_SCHEMA, '.*'), 'SCHEMA', PRIVILEGE_TYPE, IS_GRANTABLE
                FROM information_schema.SCHEMA_PRIVILEGES
                WHERE GRANTEE = '{EscapeSqlLiteral(grantee)}'
                UNION ALL
                SELECT CONCAT(TABLE_SCHEMA, '.', TABLE_NAME), 'TABLE', PRIVILEGE_TYPE, IS_GRANTABLE
                FROM information_schema.TABLE_PRIVILEGES
                WHERE GRANTEE = '{EscapeSqlLiteral(grantee)}'
                ORDER BY 2, 1, 3");

            if (!result.HasError)
            {
                foreach (var row in result.ObjectRows)
                {
                    grants.Add(new PermissionGrant
                    {
                        Grantee = userName,
                        ObjectName = GetValue(row, 0),
                        ObjectType = GetValue(row, 1),
                        Privilege = GetValue(row, 2),
                        WithGrantOption = GetValue(row, 3) == "YES"
                    });
                }
            }

            return grants;
        }

        if (session.ProviderName == "SQL Server")
        {
            var result = await session.ExecuteQueryAsync(
                $"SELECT o.name, p.permission_name, p.state_desc FROM sys.database_permissions p " +
                $"JOIN sys.database_principals u ON p.grantee_principal_id = u.principal_id " +
                $"LEFT JOIN sys.objects o ON p.major_id = o.object_id " +
                $"WHERE u.name = '{EscapeSqlLiteral(userName)}' ORDER BY o.name, p.permission_name");
            if (!result.HasError)
            {
                foreach (var row in result.ObjectRows)
                {
                    grants.Add(new PermissionGrant
                    {
                        Grantee = userName,
                        ObjectType = "OBJECT",
                        ObjectName = GetValue(row, 0),
                        Privilege = GetValue(row, 1),
                        WithGrantOption = GetValue(row, 2) == "GRANT_WITH_GRANT_OPTION"
                    });
                }
            }

            return grants;
        }

        return grants;
    }

    public Task GrantPermissionAsync(DatabaseSession session, PermissionGrant grant) =>
        session.GrantPrivilegeAsync(grant.Grantee, grant.Privilege, string.IsNullOrWhiteSpace(grant.ObjectName) ? null : grant.ObjectName);

    public Task RevokePermissionAsync(DatabaseSession session, PermissionGrant grant) =>
        session.RevokePrivilegeAsync(grant.Grantee, grant.Privilege, string.IsNullOrWhiteSpace(grant.ObjectName) ? null : grant.ObjectName);

    public Task CreateUserAsync(DatabaseSession session, string name, string password) =>
        session.CreateUserAsync(name, password);

    public Task DropUserAsync(DatabaseSession session, string name) =>
        session.DropUserAsync(name);

    public Task ChangePasswordAsync(DatabaseSession session, string name, string password) =>
        session.ChangePasswordAsync(name, password);
}
