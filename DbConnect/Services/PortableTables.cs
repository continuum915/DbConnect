using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>A CREATE TABLE written for one engine from portable columns, with what it could not reproduce.</summary>
public sealed record PortableCreateTable(string Sql, IReadOnlyList<string> Notes);

/// <summary>
/// Reads a table of one engine as portable columns and writes it as CREATE TABLE for another: column types through
/// the target's <see cref="IPortableTypeDialect"/>, NOT NULL, defaults every engine can write, identity where the target
/// can generate values, and the primary key. Everything that does not carry over is a note: what reading the source
/// dropped, and what the target's types cannot hold.
/// </summary>
public static class PortableTables
{
    public static async Task<PortableTable> ReadAsync(DatabaseSession session, DatabaseObjectIdentity table, CancellationToken cancellationToken = default)
    {
        if (session.Provider is not ITableDesignSource designs)
            throw new InvalidOperationException($"{session.ProviderName} cannot read table designs.");
        var design = await designs.GetTableDesignAsync(session.ConnectionString, table, cancellationToken);
        return From(session.ProviderName, session.Capabilities, design);
    }

    public static PortableTable From(string providerName, DatabaseProviderCapabilities capabilities, TableDesign design)
    {
        var written = capabilities.IdentityLoading?.EngineWrittenTypeNames ?? [];
        // Engine-written columns (SQL Server rowversion) hold nothing another engine can take.
        var columns = design.Columns
            .Where(c => !written.Contains(c.DataType, StringComparer.OrdinalIgnoreCase))
            .Select(c => PortableTypeReader.Read(providerName, c)).ToList();
        return new(design.Table, columns, design.PrimaryKey?.Columns ?? []);
    }

    /// <summary>CREATE TABLE <paramref name="target"/> for <paramref name="providerName"/>.</summary>
    /// <param name="constraints">Table constraints written after the key, such as foreign keys an engine cannot add later.</param>
    public static PortableCreateTable CreateTable(string providerName, DatabaseProviderCapabilities dialect, DatabaseObjectIdentity target,
        PortableTable table, IReadOnlyList<string>? constraints = null)
    {
        var types = PortableTypeDialects.For(providerName) ?? throw new NotSupportedException($"{providerName} cannot create portable tables.");
        var notes = new List<string>(table.Columns.SelectMany(c => c.Notes));
        var key = table.PrimaryKey.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var soleKey = table.PrimaryKey.Count == 1;
        var keyOnColumn = false;
        var lines = new List<string>();
        foreach (var column in table.Columns)
        {
            var inKey = key.Contains(column.Name);
            var rendered = types.Type(column, inKey);
            if (rendered.Note != null) notes.Add($"{column.Name}: {rendered.Note}");
            var line = new StringBuilder($"{dialect.QuoteIdentifier(column.Name)} {rendered.Type}");
            if (!column.IsNullable || inKey) line.Append(" NOT NULL");
            if (column.Default is { } value && Default(value, column, rendered.Type, types, dialect) is { } literal)
                line.Append(" DEFAULT ").Append(literal);
            if (column.IsIdentity)
            {
                var identity = types.Identity(column, rendered.Type, soleKey && inKey, inKey, out var note);
                if (note != null) notes.Add(note);
                if (identity != null)
                {
                    line.Append(' ').Append(identity);
                    keyOnColumn |= identity == "PRIMARY KEY";
                }
            }
            lines.Add(line.ToString());
        }
        if (table.PrimaryKey.Count > 0 && !keyOnColumn)
            lines.Add($"PRIMARY KEY ({string.Join(", ", table.PrimaryKey.Select(dialect.QuoteIdentifier))})");
        lines.AddRange(constraints ?? []);
        var sql = $"CREATE TABLE {SqlInsertTarget.ForTable(target, dialect)} (\n    {string.Join(",\n    ", lines)}\n)";
        return new(sql, notes);
    }

    private static string? Default(PortableDefault value, PortableColumn column, string renderedType, IPortableTypeDialect types,
        DatabaseProviderCapabilities dialect)
    {
        var literal = value.Kind switch
        {
            PortableDefaultKind.CurrentTimestamp => types.CurrentTimestamp(renderedType),
            PortableDefaultKind.String => SqlLiteral.Format(value.Text, dialect),
            PortableDefaultKind.Boolean => SqlLiteral.Format(value.Text == "true", dialect),
            _ => value.Text
        };
        return value.Kind == PortableDefaultKind.CurrentTimestamp || literal == null ? literal : types.Default(literal, renderedType);
    }
}
