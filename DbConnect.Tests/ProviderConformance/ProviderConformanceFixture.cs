using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests.ProviderConformance;

/// <summary>
/// A deterministic database boundary for provider conformance tests.
///
/// SQLite and DuckDB use temporary file databases so every provider call opens
/// the same database. Network-provider fixtures can be created from an explicit
/// connection string (or the documented environment variable) by the Release 0
/// integration lanes without changing the shared contract.
/// </summary>
public sealed class ProviderConformanceFixture : IAsyncDisposable
{
    private readonly string? _databaseFile;
    private bool _seeded;

    private ProviderConformanceFixture(
        IDatabaseProvider provider,
        string connectionString,
        bool isEphemeral,
        string? databaseFile)
    {
        Provider = provider;
        ConnectionString = connectionString;
        IsEphemeral = isEphemeral;
        _databaseFile = databaseFile;

        var suffix = Guid.NewGuid().ToString("N");
        TableName = $"rain_conformance_items_{suffix}";
        ViewName = $"rain_conformance_view_{suffix}";
        IndexName = $"rain_conformance_index_{suffix}";
        TriggerName = $"rain_conformance_trigger_{suffix}";
        MacroName = $"rain_conformance_add_{suffix}";
    }

    public IDatabaseProvider Provider { get; }
    public string ProviderName => Provider.ProviderName;
    public string ConnectionString { get; }
    public bool IsEphemeral { get; }
    public string DatabaseVersion { get; private set; } = "unknown";
    public string TableName { get; }
    public string ViewName { get; }
    public string IndexName { get; }
    public string TriggerName { get; }
    public string MacroName { get; }

    /// <summary>Whether this fixture can execute the shared live-engine contract locally.</summary>
    public bool IsLocal => ProviderName is "SQLite" or "DuckDB";

    /// <summary>
    /// Creates a local file-backed fixture for SQLite or DuckDB.
    /// </summary>
    public static async Task<ProviderConformanceFixture> CreateLocalAsync(string providerName)
    {
        if (providerName is not ("SQLite" or "DuckDB"))
            throw new ArgumentException("Only SQLite and DuckDB have local Release 0 fixtures.", nameof(providerName));

        var provider = new DatabaseService().GetProvider(providerName);
        var extension = providerName == "SQLite" ? ".sqlite" : ".duckdb";
        var path = Path.Combine(Path.GetTempPath(), $"rain-conformance-{Guid.NewGuid():N}{extension}");
        var fixture = new ProviderConformanceFixture(
            provider,
            $"Data Source={path}",
            isEphemeral: true,
            databaseFile: path);

        try
        {
            await fixture.InitializeAsync();
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Creates a fixture around a caller-owned connection string. The caller
    /// must provide a database where the generated object names are safe to use.
    /// </summary>
    public static async Task<ProviderConformanceFixture> CreateExternalAsync(
        string providerName,
        string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A connection string is required.", nameof(connectionString));

        var provider = new DatabaseService().GetProvider(providerName);
        var fixture = new ProviderConformanceFixture(provider, connectionString, false, null);
        try
        {
            await fixture.InitializeAsync();
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Resolves the explicit environment-variable convention used by future CI
    /// lanes. Returning null keeps local unit runs deterministic when containers
    /// are not available.
    /// </summary>
    public static string? GetEnvironmentConnectionString(string providerName)
    {
        return Environment.GetEnvironmentVariable(GetEnvironmentConnectionStringKey(providerName));
    }

    public static string GetEnvironmentConnectionStringKey(string providerName) =>
        $"DBCONNECT_CONFORMANCE_{NormalizeProviderKey(providerName)}_CONNECTION_STRING";

    public string Quote(string identifier) => Provider.Capabilities.QuoteIdentifier(identifier);

    public string ParameterPlaceholder(int ordinal) => ProviderName == "DuckDB"
        ? "?"
        : Provider.Capabilities.CreateParameterName(ordinal);

    public string QualifiedTableName => Quote(TableName);
    public string QualifiedViewName => Quote(ViewName);

    public async Task<SchemaNode?> FindObjectAsync(SchemaNodeType type, string rawName)
    {
        var roots = await Provider.GetSchemaAsync(ConnectionString);
        var pending = roots.Select(node => (Node: node, ParentName: (string?)null)).ToList();

        while (pending.Count > 0)
        {
            var (node, parentName) = pending[^1];
            pending.RemoveAt(pending.Count - 1);

            if (node.Type == type && string.Equals(node.RawName ?? node.Name, rawName, StringComparison.OrdinalIgnoreCase))
                return node;

            if (node.IsLazyLoadable)
            {
                // Some providers put the owning schema/table on the folder;
                // otherwise the traversal parent carries that context.
                var parent = node.SchemaPath ?? node.RawName ?? parentName;
                if (string.IsNullOrWhiteSpace(parent))
                    parent = node.Name;
                var children = await Provider.GetObjectsAsync(ConnectionString, parent, LazyObjectType(node.Type));
                pending.AddRange(children.Select(child => (Node: child, ParentName: (string?)parent)));
            }

            var childParent = node.Type switch
            {
                SchemaNodeType.Schema => node.SchemaPath ?? node.RawName ?? node.Name,
                SchemaNodeType.Table => QualifyParentName(node.SchemaPath, node.RawName ?? node.Name),
                _ => parentName
            };
            pending.AddRange(node.Children.Select(child => (child, childParent)));
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_seeded || _databaseFile is not null)
        {
            // Cleanup is best effort: a failing assertion should not hide the
            // original operation failure, and external fixtures are caller-owned.
            await TryExecuteAsync($"DROP VIEW {QualifiedViewName};");
            if (ProviderName == "DuckDB")
                await TryExecuteAsync($"DROP MACRO IF EXISTS {Quote(MacroName)};");
            if (Provider.Capabilities.SupportsTriggers)
                await TryExecuteAsync($"DROP TRIGGER {Quote(TriggerName)};");
            await TryExecuteAsync(DropIndexStatement());
            await TryExecuteAsync($"DROP TABLE {QualifiedTableName};");
        }

        if (_databaseFile is not null)
        {
            try { File.Delete(_databaseFile); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task InitializeAsync()
    {
        var test = await Provider.TestConnectionAsync(ConnectionString);
        if (!test.Success)
            throw new InvalidOperationException(Context("connection", $"{test.CategoryLabel}: {test.Summary} {test.Detail}"));

        DatabaseVersion = await Provider.GetServerVersionAsync(ConnectionString);
        if (string.IsNullOrWhiteSpace(DatabaseVersion) || DatabaseVersion.Contains("unknown", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Context("server version", "The provider returned no usable version."));

        _seeded = true;
        await SeedAsync();
    }

    private async Task SeedAsync()
    {
        await Provider.ExecuteNonQueryAsync(ConnectionString, $"""
            CREATE TABLE {QualifiedTableName} (
                {Quote("id")} INTEGER PRIMARY KEY,
                {Quote("name")} VARCHAR(100) NULL,
                {Quote("amount")} INTEGER NOT NULL
            );
            """);

        await Provider.ExecuteNonQueryAsync(ConnectionString, $"CREATE INDEX {Quote(IndexName)} ON {QualifiedTableName} ({Quote("name")});");
        await Provider.ExecuteNonQueryAsync(ConnectionString, $"CREATE VIEW {QualifiedViewName} AS SELECT {Quote("id")}, {Quote("name")} FROM {QualifiedTableName};");

        await Provider.ExecuteNonQueryAsync(ConnectionString, $"INSERT INTO {QualifiedTableName} ({Quote("id")}, {Quote("name")}, {Quote("amount")}) VALUES (1, 'Ada', 10);");
        await Provider.ExecuteNonQueryAsync(ConnectionString, $"INSERT INTO {QualifiedTableName} ({Quote("id")}, {Quote("name")}, {Quote("amount")}) VALUES (2, 'Grace', 20);");

        if (ProviderName == "DuckDB")
        {
            await Provider.ExecuteNonQueryAsync(ConnectionString, $"CREATE MACRO {Quote(MacroName)}(a, b) AS a + b;");
        }

        if (ProviderName == "SQLite" && Provider.Capabilities.SupportsTriggers)
        {
            await Provider.ExecuteNonQueryAsync(ConnectionString, $"""
                CREATE TRIGGER {Quote(TriggerName)} AFTER INSERT ON {QualifiedTableName}
                BEGIN
                    UPDATE {QualifiedTableName} SET {Quote("amount")} = {Quote("amount")} WHERE {Quote("id")} = NEW.{Quote("id")};
                END;
                """);
        }
    }

    private async Task TryExecuteAsync(string sql)
    {
        try { await Provider.ExecuteNonQueryAsync(ConnectionString, sql); }
        catch { }
    }

    private string Context(string operation, string detail) =>
        $"{ProviderName} {DatabaseVersion} [{operation}]: {detail}";

    private string DropIndexStatement() => ProviderName switch
    {
        "SQL Server" => $"DROP INDEX {Quote(IndexName)} ON {QualifiedTableName};",
        "MySQL" => $"DROP INDEX {Quote(IndexName)} ON {QualifiedTableName};",
        _ => $"DROP INDEX IF EXISTS {Quote(IndexName)};"
    };

    private SchemaNodeType LazyObjectType(SchemaNodeType folderType) => folderType switch
    {
        SchemaNodeType.TableFolder => ProviderName == "DuckDB" ? SchemaNodeType.TableFolder : SchemaNodeType.Table,
        SchemaNodeType.ViewFolder => ProviderName == "DuckDB" ? SchemaNodeType.ViewFolder : SchemaNodeType.View,
        SchemaNodeType.IndexFolder => ProviderName is "DuckDB" or "MySQL" ? SchemaNodeType.IndexFolder : SchemaNodeType.Index,
        SchemaNodeType.TriggerFolder => ProviderName is "DuckDB" or "MySQL" ? SchemaNodeType.TriggerFolder : SchemaNodeType.Trigger,
        SchemaNodeType.FunctionFolder => ProviderName == "DuckDB" ? SchemaNodeType.FunctionFolder : SchemaNodeType.Function,
        SchemaNodeType.ProcedureFolder => ProviderName == "DuckDB" ? SchemaNodeType.ProcedureFolder : SchemaNodeType.Procedure,
        SchemaNodeType.ForeignKeyFolder => ProviderName == "SQLite" ? SchemaNodeType.ForeignKey : SchemaNodeType.ForeignKeyFolder,
        _ => folderType
    };

    private static string QualifyParentName(string? schemaPath, string objectName) =>
        string.IsNullOrWhiteSpace(schemaPath) ? objectName : $"{schemaPath}.{objectName}";

    private static string NormalizeProviderKey(string providerName) =>
        new string(providerName.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
}
