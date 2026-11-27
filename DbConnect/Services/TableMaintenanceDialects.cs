using System;
using System.Collections.Generic;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>SQL for explorer maintenance actions on one engine. Null means the engine cannot do it.</summary>
public interface ITableMaintenanceDialect
{
    /// <summary>TRUNCATE for the table, or null when the engine has none (use Empty instead).</summary>
    string? Truncate(DatabaseObjectIdentity table);

    /// <summary>Note shown with the TRUNCATE preview, such as an implicit commit.</summary>
    string? TruncateNote { get; }

    string Empty(DatabaseObjectIdentity table) => $"DELETE FROM {Qualified(table)}";

    /// <summary>Rename DDL, or null with <paramref name="reason"/> when this kind cannot be renamed.</summary>
    string? Rename(DatabaseObjectIdentity table, string newName, out string? reason);

    string Qualified(DatabaseObjectIdentity table);
}

/// <summary>Maintenance dialects by provider, mirroring <see cref="Builders.DdlBuilderFactory"/>.</summary>
public static class TableMaintenanceDialects
{
    private static readonly Dictionary<string, ITableMaintenanceDialect> Dialects = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SQLite"] = new SqliteMaintenance(),
        ["PostgreSQL"] = new PostgreSqlMaintenance(),
        ["SQL Server"] = new SqlServerMaintenance(),
        ["MySQL"] = new MySqlMaintenance(),
        ["DuckDB"] = new DuckDbMaintenance()
    };

    public static ITableMaintenanceDialect? For(string providerName) => Dialects.GetValueOrDefault(providerName);

    /// <summary>Schema when the engine has one, otherwise the catalog (MySQL databases).</summary>
    private abstract class Dialect(DatabaseProviderCapabilities capabilities) : ITableMaintenanceDialect
    {
        protected string Quote(string name) => capabilities.QuoteIdentifier(name);

        public string Qualified(DatabaseObjectIdentity table) =>
            (table.Schema ?? table.Catalog) is { } owner ? Quote(owner) + "." + Quote(table.Name) : Quote(table.Name);

        public virtual string? Truncate(DatabaseObjectIdentity table) => $"TRUNCATE TABLE {Qualified(table)}";
        public virtual string? TruncateNote => null;
        public abstract string? Rename(DatabaseObjectIdentity table, string newName, out string? reason);

        protected static string Keyword(SchemaNodeType kind) => kind switch
        {
            SchemaNodeType.View => "VIEW",
            SchemaNodeType.MaterializedView => "MATERIALIZED VIEW",
            _ => "TABLE"
        };
    }

    private sealed class SqliteMaintenance() : Dialect(ProviderCapabilityDeclarations.Sqlite)
    {
        public override string? Truncate(DatabaseObjectIdentity table) => null;

        public override string? Rename(DatabaseObjectIdentity table, string newName, out string? reason)
        {
            reason = table.Kind == SchemaNodeType.Table ? null : "SQLite cannot rename views; drop and recreate the view instead.";
            return reason == null ? $"ALTER TABLE {Qualified(table)} RENAME TO {Quote(newName)}" : null;
        }
    }

    private sealed class PostgreSqlMaintenance() : Dialect(ProviderCapabilityDeclarations.PostgreSql)
    {
        public override string? Rename(DatabaseObjectIdentity table, string newName, out string? reason)
        {
            reason = null;
            return $"ALTER {Keyword(table.Kind)} {Qualified(table)} RENAME TO {Quote(newName)}";
        }
    }

    private sealed class SqlServerMaintenance() : Dialect(ProviderCapabilityDeclarations.SqlServer)
    {
        public override string? TruncateNote =>
            "SQL Server refuses TRUNCATE on a table that a foreign key references; use Empty Table instead.";

        public override string? Rename(DatabaseObjectIdentity table, string newName, out string? reason)
        {
            reason = null;
            // sp_rename takes the new name literally: it must not be bracket-quoted or schema-qualified.
            return $"EXEC sp_rename {Literal(Qualified(table))}, {Literal(newName)}";
        }

        private static string Literal(string value) => "N'" + value.Replace("'", "''") + "'";
    }

    private sealed class MySqlMaintenance() : Dialect(ProviderCapabilityDeclarations.MySql)
    {
        public override string? TruncateNote =>
            "MySQL commits any open transaction before TRUNCATE, resets AUTO_INCREMENT, and refuses it on a table that a foreign key references.";

        public override string? Rename(DatabaseObjectIdentity table, string newName, out string? reason)
        {
            reason = null;
            var owner = table.Catalog ?? table.Schema;
            var target = owner == null ? Quote(newName) : Quote(owner) + "." + Quote(newName);
            return $"RENAME TABLE {Qualified(table)} TO {target}";
        }
    }

    private sealed class DuckDbMaintenance() : Dialect(ProviderCapabilityDeclarations.DuckDb)
    {
        public override string? Rename(DatabaseObjectIdentity table, string newName, out string? reason)
        {
            reason = table.Kind == SchemaNodeType.MaterializedView ? "DuckDB has no materialized views." : null;
            return reason == null ? $"ALTER {Keyword(table.Kind)} {Qualified(table)} RENAME TO {Quote(newName)}" : null;
        }
    }
}
