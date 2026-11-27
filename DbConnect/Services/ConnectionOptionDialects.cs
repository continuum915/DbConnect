using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// How one provider applies <see cref="ConnectionOptions"/>: driver keywords go into the connection string,
/// and statements that only a session can run (startup SQL, some read-only and schema settings) run when a
/// query or table session opens. The dialog shows only the options a provider declares.
/// </summary>
public interface IConnectionOptionsDialect
{
    /// <summary>Sign-in modes the connection dialog offers; one entry means there is no choice.</summary>
    IReadOnlyList<ConnectionAuthentication> AuthenticationModes { get; }
    bool SupportsConnectTimeout { get; }
    bool SupportsKeepAlive { get; }
    bool SupportsDefaultSchema { get; }
    /// <summary>What the server does for a read-only connection, shown next to the checkbox.</summary>
    string ReadOnlyEnforcement { get; }

    /// <summary>The connection string with the options' driver keywords applied.</summary>
    /// <exception cref="InvalidOperationException">An additional property is unknown to the driver or holds a secret.</exception>
    string Apply(string connectionString, ConnectionOptions options);

    /// <summary>Statements run on each new session, in order, before it is used.</summary>
    IReadOnlyList<string> SessionSetup(ConnectionOptions options, SqlBatchDialect dialect) =>
        SplitStartup(options, dialect);

    static IReadOnlyList<string> SplitStartup(ConnectionOptions options, SqlBatchDialect dialect, params string?[] first) =>
        first.OfType<string>().Concat(string.IsNullOrWhiteSpace(options.StartupSql) ? []
            : SqlBatchParser.SplitStatements(options.StartupSql, dialect).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()))
        .ToList();
}

public static class ConnectionOptionDialects
{
    public static IConnectionOptionsDialect? For(string providerName) => providerName switch
    {
        "SQLite" => new SqliteOptions(),
        "DuckDB" => new DuckDbOptions(),
        "PostgreSQL" => new PostgreSqlOptions(),
        "SQL Server" => new SqlServerOptions(),
        "MySQL" => new MySqlOptions(),
        _ => null
    };

    /// <summary>Keys that would put a secret in saved metadata; secrets belong in the password field.</summary>
    public static bool IsSecretKey(string key) =>
        new[] { "password", "pwd", "secret", "token" }.Any(word => key.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static string WithProperties(DbConnectionStringBuilder builder, ConnectionOptions options)
    {
        foreach (var property in options.AdditionalProperties.Where(p => !string.IsNullOrWhiteSpace(p.Key)))
        {
            if (IsSecretKey(property.Key))
                throw new InvalidOperationException($"Driver property {property.Key} holds a secret; use the password field instead.");
            try { builder[property.Key.Trim()] = property.Value; }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"The driver does not accept property {property.Key}: {ex.Message}", ex);
            }
        }
        return builder.ConnectionString;
    }

    private static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

    private sealed class SqliteOptions : IConnectionOptionsDialect
    {
        public IReadOnlyList<ConnectionAuthentication> AuthenticationModes => [];
        public bool SupportsConnectTimeout => false;
        public bool SupportsKeepAlive => false;
        public bool SupportsDefaultSchema => false;
        public string ReadOnlyEnforcement => "The database file is opened read-only.";

        public string Apply(string connectionString, ConnectionOptions options)
        {
            var builder = new SqliteConnectionStringBuilder(connectionString);
            if (options.IsReadOnly) builder.Mode = SqliteOpenMode.ReadOnly;
            if (options.CommandTimeoutSeconds is { } timeout) builder.DefaultTimeout = timeout;
            return WithProperties(builder, options);
        }
    }

    private sealed class DuckDbOptions : IConnectionOptionsDialect
    {
        public IReadOnlyList<ConnectionAuthentication> AuthenticationModes => [];
        public bool SupportsConnectTimeout => false;
        public bool SupportsKeepAlive => false;
        public bool SupportsDefaultSchema => true;
        public string ReadOnlyEnforcement => "The database file is opened read-only.";

        public string Apply(string connectionString, ConnectionOptions options)
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            if (options.IsReadOnly) builder["ACCESS_MODE"] = "READ_ONLY";
            return WithProperties(builder, options);
        }

        public IReadOnlyList<string> SessionSetup(ConnectionOptions options, SqlBatchDialect dialect) =>
            IConnectionOptionsDialect.SplitStartup(options, dialect,
                string.IsNullOrWhiteSpace(options.DefaultSchema) ? null : $"SET schema = {Literal(options.DefaultSchema.Trim())}");
    }

    private sealed class PostgreSqlOptions : IConnectionOptionsDialect
    {
        public IReadOnlyList<ConnectionAuthentication> AuthenticationModes =>
            [ConnectionAuthentication.Password, ConnectionAuthentication.Integrated];
        public bool SupportsConnectTimeout => true;
        public bool SupportsKeepAlive => true;
        public bool SupportsDefaultSchema => true;
        public string ReadOnlyEnforcement => "The server starts every transaction read-only (default_transaction_read_only).";

        public string Apply(string connectionString, ConnectionOptions options)
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            if (options.ConnectTimeoutSeconds is { } connect) builder.Timeout = connect;
            if (options.CommandTimeoutSeconds is { } command) builder.CommandTimeout = command;
            if (options.KeepAliveSeconds is { } keepAlive) builder.KeepAlive = keepAlive;
            if (!string.IsNullOrWhiteSpace(options.DefaultSchema)) builder.SearchPath = options.DefaultSchema.Trim();
            if (options.IsReadOnly)
                builder.Options = $"{builder.Options} -c default_transaction_read_only=on".Trim();
            // GSS/SSPI is negotiated by Npgsql when no password is sent.
            if (options.Authentication == ConnectionAuthentication.Integrated) builder.Password = null;
            return WithProperties(builder, options);
        }
    }

    private sealed class SqlServerOptions : IConnectionOptionsDialect
    {
        public IReadOnlyList<ConnectionAuthentication> AuthenticationModes => Enum.GetValues<ConnectionAuthentication>();
        public bool SupportsConnectTimeout => true;
        public bool SupportsKeepAlive => false;
        public bool SupportsDefaultSchema => false;
        public string ReadOnlyEnforcement =>
            "SQL Server has no read-only session: DbConnect refuses writes and sends ApplicationIntent=ReadOnly, which only routes to readable replicas.";

        public string Apply(string connectionString, ConnectionOptions options)
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            if (options.ConnectTimeoutSeconds is { } connect) builder.ConnectTimeout = connect;
            if (options.CommandTimeoutSeconds is { } command) builder.CommandTimeout = command;
            if (options.IsReadOnly) builder.ApplicationIntent = ApplicationIntent.ReadOnly;
            switch (options.Authentication)
            {
                case ConnectionAuthentication.Integrated:
                    builder.IntegratedSecurity = true;
                    builder.UserID = string.Empty;
                    builder.Password = string.Empty;
                    break;
                case ConnectionAuthentication.EntraInteractive:
                    builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryInteractive;
                    builder.Password = string.Empty;
                    break;
                case ConnectionAuthentication.EntraIntegrated:
                    builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryIntegrated;
                    builder.UserID = string.Empty;
                    builder.Password = string.Empty;
                    break;
                case ConnectionAuthentication.EntraPassword:
                    builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryPassword;
                    break;
                case ConnectionAuthentication.EntraServicePrincipal:
                    builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryServicePrincipal;
                    break;
                case ConnectionAuthentication.EntraManagedIdentity:
                    builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryManagedIdentity;
                    builder.Password = string.Empty;
                    break;
                case ConnectionAuthentication.EntraDefault:
                    builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryDefault;
                    builder.Password = string.Empty;
                    break;
            }
            return WithProperties(builder, options);
        }
    }

    private sealed class MySqlOptions : IConnectionOptionsDialect
    {
        public IReadOnlyList<ConnectionAuthentication> AuthenticationModes => [ConnectionAuthentication.Password];
        public bool SupportsConnectTimeout => true;
        public bool SupportsKeepAlive => true;
        public bool SupportsDefaultSchema => false;
        public string ReadOnlyEnforcement => "Each query and table session runs SET SESSION TRANSACTION READ ONLY.";

        public string Apply(string connectionString, ConnectionOptions options)
        {
            var builder = new MySqlConnectionStringBuilder(connectionString);
            if (options.ConnectTimeoutSeconds is { } connect) builder.ConnectionTimeout = (uint)connect;
            if (options.CommandTimeoutSeconds is { } command) builder.DefaultCommandTimeout = (uint)command;
            if (options.KeepAliveSeconds is { } keepAlive) builder.Keepalive = (uint)keepAlive;
            return WithProperties(builder, options);
        }

        public IReadOnlyList<string> SessionSetup(ConnectionOptions options, SqlBatchDialect dialect) =>
            IConnectionOptionsDialect.SplitStartup(options, dialect,
                options.IsReadOnly ? "SET SESSION TRANSACTION READ ONLY" : null);
    }
}
