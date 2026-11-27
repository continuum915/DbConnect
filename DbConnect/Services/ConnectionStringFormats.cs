using System;
using System.Collections.Generic;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;

namespace DbConnect.Services;

/// <summary>The connection details the connection dialog edits, independent of any driver's key names.</summary>
public sealed record ConnectionFields
{
    /// <summary>The database file, for a format that <see cref="IConnectionStringFormat.UsesDatabaseFile"/>.</summary>
    public string FilePath { get; init; } = string.Empty;
    public string Host { get; init; } = string.Empty;
    public int? Port { get; init; }
    public string Database { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    /// <summary>
    /// The provider's own SSL mode name: PostgreSQL Disable/Prefer/Require/VerifyFull, SQL Server
    /// Optional/Required/Strict, MySQL None/Preferred/Required/VerifyCA/VerifyFull.
    /// Null for a provider the dialog offers no SSL choice for.
    /// </summary>
    public string? SslMode { get; init; }
    public string SslCaCertPath { get; init; } = string.Empty;
    public string SslClientCertPath { get; init; } = string.Empty;
    public string SslClientKeyPath { get; init; } = string.Empty;
    /// <summary>SQL Server only; the others report the default.</summary>
    public bool TrustServerCertificate { get; init; }
}

/// <summary>Maps <see cref="ConnectionFields"/> to and from one provider's connection string.</summary>
public interface IConnectionStringFormat
{
    /// <summary>Whether a connection opens a database file (SQLite, DuckDB) rather than a server.</summary>
    bool UsesDatabaseFile { get; }
    /// <summary>The server's standard port; null for a file database.</summary>
    int? DefaultPort { get; }

    bool SupportsCaCertificate => false;
    bool SupportsClientCertificate => false;
    bool SupportsClientKey => false;
    IReadOnlyList<string> SslModes => Array.Empty<string>();

    string? DefaultSslMode => Parse(string.Empty).SslMode;

    string Build(ConnectionFields fields);
    ConnectionFields Parse(string connectionString);
    /// <summary>Points the connection string at <paramref name="host"/>:<paramref name="port"/>, keeping every other setting.</summary>
    string WithServer(string connectionString, string host, int port);

    /// <summary>
    /// Splits the password out of a connection string so it can be kept in the credential store.
    /// A format without passwords returns the string unchanged and an empty password.
    /// </summary>
    (string ConnectionString, string Password) WithoutPassword(string connectionString) => (connectionString, string.Empty);

    /// <summary>Puts a password from the credential store back into a connection string.</summary>
    string WithPassword(string connectionString, string password) => connectionString;
}

/// <summary>
/// architecture-discipline ticket 01 — one connection string format per provider, so the connection
/// dialog and the SSH tunnel need no provider checks. Every registered provider must have one.
/// </summary>
public static class ConnectionStringFormats
{
    public static IConnectionStringFormat? For(string providerName) => providerName switch
    {
        "SQLite" => new SqliteFormat(),
        "DuckDB" => new DuckDbFormat(),
        "PostgreSQL" => new PostgreSqlFormat(),
        "SQL Server" => new SqlServerFormat(),
        "MySQL" => new MySqlFormat(),
        _ => null
    };

    private sealed class SqliteFormat : IConnectionStringFormat
    {
        public bool UsesDatabaseFile => true;
        public int? DefaultPort => null;

        public string Build(ConnectionFields fields) =>
            new SqliteConnectionStringBuilder { DataSource = fields.FilePath }.ConnectionString;

        public ConnectionFields Parse(string connectionString) =>
            new() { FilePath = new SqliteConnectionStringBuilder(connectionString).DataSource ?? string.Empty };

        public string WithServer(string connectionString, string host, int port) => connectionString;
    }

    private sealed class DuckDbFormat : IConnectionStringFormat
    {
        public bool UsesDatabaseFile => true;
        public int? DefaultPort => null;

        private const string DataSource = "Data Source";

        public string Build(ConnectionFields fields) =>
            new DbConnectionStringBuilder { [DataSource] = fields.FilePath }.ConnectionString;

        public ConnectionFields Parse(string connectionString)
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            return new() { FilePath = builder.TryGetValue(DataSource, out var path) ? path?.ToString() ?? string.Empty : string.Empty };
        }

        public string WithServer(string connectionString, string host, int port) => connectionString;
    }

    private sealed class PostgreSqlFormat : IConnectionStringFormat
    {
        public string DefaultSslMode => "VerifyFull";
        public bool SupportsCaCertificate => true;
        public bool SupportsClientCertificate => true;
        public bool SupportsClientKey => true;
        public IReadOnlyList<string> SslModes => ["Disable", "Allow", "Prefer", "Require", "VerifyCA", "VerifyFull"];
        public bool UsesDatabaseFile => false;
        public int? DefaultPort => NpgsqlConnection.DefaultPort;

        public string Build(ConnectionFields fields) => new NpgsqlConnectionStringBuilder
        {
            Host = fields.Host,
            Port = fields.Port ?? NpgsqlConnection.DefaultPort,
            Database = fields.Database,
            Username = fields.Username,
            Password = fields.Password,
            RootCertificate = fields.SslCaCertPath,
            SslCertificate = fields.SslClientCertPath,
            SslKey = fields.SslClientKeyPath,
            SslMode = Enum.TryParse<SslMode>(fields.SslMode, out var mode) ? mode : SslMode.VerifyFull
        }.ConnectionString;

        public ConnectionFields Parse(string connectionString)
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            return new()
            {
                Host = builder.Host ?? string.Empty,
                Port = builder.Port,
                Database = builder.Database ?? string.Empty,
                Username = builder.Username ?? string.Empty,
                Password = builder.Password ?? string.Empty,
                SslCaCertPath = builder.RootCertificate ?? string.Empty,
                SslClientCertPath = builder.SslCertificate ?? string.Empty,
                SslClientKeyPath = builder.SslKey ?? string.Empty,
                SslMode = builder.SslMode.ToString()
            };
        }

        public string WithServer(string connectionString, string host, int port) =>
            new NpgsqlConnectionStringBuilder(connectionString) { Host = host, Port = port }.ConnectionString;

        public (string ConnectionString, string Password) WithoutPassword(string connectionString)
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            var password = builder.Password ?? string.Empty;
            builder.Password = string.Empty;
            return (builder.ConnectionString, password);
        }

        public string WithPassword(string connectionString, string password) =>
            new NpgsqlConnectionStringBuilder(connectionString) { Password = password }.ConnectionString;
    }

    private sealed class SqlServerFormat : IConnectionStringFormat
    {
        public string DefaultSslMode => "Required";
        private const int StandardPort = 1433;

        public bool UsesDatabaseFile => false;
        public int? DefaultPort => StandardPort;

        public string Build(ConnectionFields fields) => new SqlConnectionStringBuilder
        {
            DataSource = fields.Port is { } port ? $"{fields.Host},{port}" : fields.Host,
            InitialCatalog = fields.Database,
            UserID = fields.Username,
            Password = fields.Password,
            Encrypt = fields.SslMode switch
            {
                null or "Required" => SqlConnectionEncryptOption.Mandatory,
                "Strict" => SqlConnectionEncryptOption.Strict,
                _ => SqlConnectionEncryptOption.Optional
            },
            TrustServerCertificate = fields.TrustServerCertificate
        }.ConnectionString;

        public ConnectionFields Parse(string connectionString)
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            // DataSource is "host,port"; split it to recover each.
            var dataSource = builder.DataSource ?? string.Empty;
            var comma = dataSource.IndexOf(',');
            var host = comma >= 0 ? dataSource[..comma].Trim() : dataSource;
            var port = comma >= 0 && int.TryParse(dataSource[(comma + 1)..].Trim(), out var explicitPort) ? explicitPort : StandardPort;
            return new()
            {
                Host = host,
                Port = port,
                Database = builder.InitialCatalog ?? string.Empty,
                Username = builder.UserID ?? string.Empty,
                Password = builder.Password ?? string.Empty,
                SslMode = builder.Encrypt == SqlConnectionEncryptOption.Mandatory ? "Required"
                    : builder.Encrypt == SqlConnectionEncryptOption.Strict ? "Strict"
                    : "Optional",
                TrustServerCertificate = builder.TrustServerCertificate
            };
        }

        public string WithServer(string connectionString, string host, int port) =>
            new SqlConnectionStringBuilder(connectionString) { DataSource = $"{host},{port}" }.ConnectionString;

        public (string ConnectionString, string Password) WithoutPassword(string connectionString)
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            var password = builder.Password ?? string.Empty;
            builder.Password = string.Empty;
            return (builder.ConnectionString, password);
        }

        public string WithPassword(string connectionString, string password) =>
            new SqlConnectionStringBuilder(connectionString) { Password = password }.ConnectionString;
    }

    private sealed class MySqlFormat : IConnectionStringFormat
    {
        public bool SupportsCaCertificate => true;
        public bool SupportsClientCertificate => true;
        public bool SupportsClientKey => true;
        public IReadOnlyList<string> SslModes => ["None", "Preferred", "Required", "VerifyCA", "VerifyFull"];
        private const uint StandardPort = 3306;

        public bool UsesDatabaseFile => false;
        public int? DefaultPort => (int)StandardPort;

        public string Build(ConnectionFields fields) => new MySqlConnectionStringBuilder
        {
            Server = fields.Host,
            Port = fields.Port is { } port ? (uint)port : StandardPort,
            Database = fields.Database,
            UserID = fields.Username,
            Password = fields.Password,
            SslCa = fields.SslCaCertPath,
            SslCert = fields.SslClientCertPath,
            SslKey = fields.SslClientKeyPath,
            SslMode = Enum.TryParse<MySqlSslMode>(fields.SslMode, out var mode) ? mode : MySqlSslMode.Preferred
        }.ConnectionString;

        public ConnectionFields Parse(string connectionString)
        {
            var builder = new MySqlConnectionStringBuilder(connectionString);
            return new()
            {
                Host = builder.Server ?? string.Empty,
                Port = (int)builder.Port,
                Database = builder.Database ?? string.Empty,
                Username = builder.UserID ?? string.Empty,
                Password = builder.Password ?? string.Empty,
                SslCaCertPath = builder.SslCa ?? string.Empty,
                SslClientCertPath = builder.SslCert ?? string.Empty,
                SslClientKeyPath = builder.SslKey ?? string.Empty,
                SslMode = builder.SslMode.ToString()
            };
        }

        public string WithServer(string connectionString, string host, int port) =>
            new MySqlConnectionStringBuilder(connectionString) { Server = host, Port = (uint)port }.ConnectionString;

        public (string ConnectionString, string Password) WithoutPassword(string connectionString)
        {
            var builder = new MySqlConnectionStringBuilder(connectionString);
            var password = builder.Password ?? string.Empty;
            builder.Password = string.Empty;
            return (builder.ConnectionString, password);
        }

        public string WithPassword(string connectionString, string password) =>
            new MySqlConnectionStringBuilder(connectionString) { Password = password }.ConnectionString;
    }
}
