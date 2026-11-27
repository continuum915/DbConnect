using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>A table could not be dumped; the message names it. Nothing is published.</summary>
public sealed class DatabaseDumpException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Dump SQL File, as Navicat names it: one same-provider <c>.sql</c> file that recreates the selected tables, parents
/// before the tables that reference them. Each table is written from its Script as CREATE snapshot; with data, every
/// table's rows follow all the structures as INSERT statements, and triggers come last so loading does not fire them.
/// Foreign-key cycles and self-references load as <see cref="DumpCycleLoading"/> plans them: their keys are added after
/// the data, or foreign-key checks are off for the load.
/// The header states what the file includes, excludes and requires. A table that cannot be scripted or read fails the
/// whole dump, and the file is published only when every table was written.
/// </summary>
public static class DatabaseDumpService
{
    public static ExportFileType FileType { get; } = new("sql", "SQL Files", "application/sql");

    public static async Task<ExportOutcome> DumpToFileAsync(DatabaseSession session, DumpSelection selection, DumpMode mode,
        string path, IExportFileOperations? files = null, IProgress<DumpProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        var file = new OwnedExportFile(files ?? new LocalExportFileOperations(), path, FileType.Extension);
        string? diagnostic = null;
        var status = ExportStatus.Failed;
        long rows = 0;
        try
        {
            await file.WriteAndPublishAsync(async stream => rows = await WriteAsync(session, selection, mode, stream, progress, cancellationToken),
                cancellationToken);
            status = ExportStatus.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { status = ExportStatus.Canceled; }
        catch (DatabaseDumpException error) { diagnostic = error.Message + " Destination was not published."; }
        catch (Exception error)
        {
            diagnostic = $"Dump failed ({error.GetType().Name}): {DiagnosticRedactor.Redact(error.Message)} Destination was not published.";
        }
        return new(status, ExportScope.DatabaseDump, ExportFormat.SqlInsert, status == ExportStatus.Completed ? rows : 0, clock.Elapsed,
            status == ExportStatus.Completed ? file.Destination : null, diagnostic, file.RetainedTemporaryPath);
    }

    /// <summary>Writes the dump to <paramref name="output"/> and returns the number of table rows written.</summary>
    public static async Task<long> WriteAsync(DatabaseSession session, DumpSelection selection, DumpMode mode, Stream output,
        IProgress<DumpProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (session.Provider is not ITableScriptSource source)
            throw new DatabaseDumpException($"{session.ProviderName} cannot script complete table definitions, so it cannot be dumped.");
        var catalog = await DumpCatalog.ResolveAsync(session, selection);
        var tables = catalog.Tables;
        await using var db = new ExportDbSession(await OpenAsync(session, cancellationToken));
        var foreignKeys = await LoadForeignKeysAsync(db);
        var snapshots = new List<TableDefinitionSnapshot>();
        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshots.Add(await SnapshotAsync(source, session, table, cancellationToken));
            progress?.Report(new(snapshots.Count, tables.Count, ObjectNames.SchemaQualified(table)));
        }
        var objects = catalog.Objects.Count > 0 || catalog.ListingFailures.Count > 0
            ? await DumpObjects.ReadAsync(session, catalog, cancellationToken)
            : DumpObjects.None;
        var order = DumpTableOrder.Sort(snapshots, foreignKeys);
        var loading = DumpCycleLoading.Plan(order, session.Capabilities, session.ProviderName, mode);
        var data = mode == DumpMode.StructureAndData ? new DumpTableData(session, db) : null;
        try
        {
            if (data != null)
            {
                await data.BeginAsync(cancellationToken);
                foreach (var (table, key) in loading.OrderChecked) await data.CheckLoadOrderAsync(table.Table, key, cancellationToken);
            }
            return await WriteScriptAsync(session, mode, new(order.Tables, objects, loading), data, output, progress, cancellationToken);
        }
        finally
        {
            if (data != null) await data.EndAsync();
        }
    }

    /// <summary>What the script recreates, in order: tables, then views and routines, then triggers.</summary>
    private sealed record DumpContents(IReadOnlyList<TableDefinitionSnapshot> Tables, DumpObjects Objects, DumpCycleLoading Loading);

    private static async Task<long> WriteScriptAsync(DatabaseSession session, DumpMode mode, DumpContents contents, DumpTableData? data,
        Stream output, IProgress<DumpProgress>? progress, CancellationToken cancellationToken)
    {
        static bool IsTrigger(TableDefinitionPart part) => part.Kind == TableDefinitionPartKind.Trigger;
        var (tables, objects, loading) = contents;
        var rules = session.Capabilities.ScriptRules;
        var separator = rules.BatchSeparator;
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), 65536, leaveOpen: true) { NewLine = "\n" };
        // SQL Server keeps the whole batch as a view's, routine's or trigger's text, so a module's heading ends its own batch.
        Task HeadingAsync(string title, bool module) =>
            writer.WriteAsync(module && separator != null ? $"\n-- {title}\n{separator}\n" : $"\n-- {title}\n");
        async Task SectionAsync(string title, TableDefinitionSnapshot table, Func<TableDefinitionPart, bool>? include = null, bool module = false)
        {
            var statements = table.OrderedStatements(include);
            if (statements.Length == 0) return;
            await HeadingAsync($"{title} {ObjectNames.SchemaQualified(table.Table)}", module);
            // Verbatim, as objects are: an engine's stored text (SQLite's) may hold a CRLF inside a CHECK or DEFAULT literal.
            await writer.WriteAsync(statements + "\n");
        }
        async Task ObjectAsync(DumpObject item, string title)
        {
            await HeadingAsync(title, true);
            // Verbatim: a line break inside a string literal of the definition is part of its value.
            await writer.WriteAsync(item.Statement(rules) + "\n");
        }

        await writer.WriteAsync(Header(session, mode, contents, data?.SnapshotDescription));
        if (loading.ChecksOff is { } checks) await writer.WriteAsync($"\n{checks.Off}\n");
        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SectionAsync("Table", table, part => !loading.IsDeferred(table, part) && !IsTrigger(part));
        }

        long rows = 0;
        for (var i = 0; data != null && i < tables.Count; i++)
        {
            var name = ObjectNames.SchemaQualified(tables[i].Table);
            await writer.WriteAsync($"\n-- Data {name}\n");
            await writer.FlushAsync(cancellationToken);
            var number = i + 1;
            rows += await data.WriteAsync(tables[i].Table, output, written => progress?.Report(new(number, tables.Count, name, written)),
                cancellationToken);
        }
        // A cycle's foreign keys are added once every table in it exists and holds its rows.
        foreach (var table in tables.Where(loading.HasDeferred))
            await SectionAsync("Foreign keys", table, part => loading.IsDeferred(table, part));
        // Views and routines may read any table, and triggers may call routines, so they follow every table.
        foreach (var item in objects.ViewsAndRoutines) await ObjectAsync(item, $"{item.Title} {ObjectNames.SchemaQualified(item.Identity)}");
        foreach (var table in tables) await SectionAsync("Triggers", table, IsTrigger, module: true);
        foreach (var item in objects.Triggers)
            await ObjectAsync(item, $"Trigger {ObjectNames.SchemaQualified(item.Identity)} on {ObjectNames.SchemaQualified(item.Identity.Parent!)}");
        if (loading.ChecksOff is { } restore) await writer.WriteAsync($"\n{restore.On}\n");
        await writer.FlushAsync(cancellationToken);
        return rows;
    }

    private static async Task<TableDefinitionSnapshot> SnapshotAsync(ITableScriptSource source, DatabaseSession session,
        DatabaseObjectIdentity table, CancellationToken cancellationToken)
    {
        try
        {
            return await source.GetTableSnapshotAsync(session.ConnectionString, table, cancellationToken);
        }
        catch (TableDefinitionUnavailableException ex)
        {
            throw new DatabaseDumpException($"Table {ObjectNames.SchemaQualified(table)} could not be scripted: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new DatabaseDumpException($"Table {ObjectNames.SchemaQualified(table)} could not be scripted: {DiagnosticRedactor.Redact(ex.Message)}", ex);
        }
    }

    private static async Task<IDbSession> OpenAsync(DatabaseSession session, CancellationToken cancellationToken)
    {
        try
        {
            return await SessionSetup.OpenAsync(session, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new DatabaseDumpException($"The database could not be opened for the dump: {DiagnosticRedactor.Redact(ex.Message)}", ex);
        }
    }

    private static async Task<IReadOnlyList<ForeignKeyRelationship>> LoadForeignKeysAsync(IDbSession db)
    {
        try
        {
            return await ForeignKeyService.LoadAsync(db);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new DatabaseDumpException($"Foreign keys could not be read to order the tables: {DiagnosticRedactor.Redact(ex.Message)}", ex);
        }
    }

    private static string Header(DatabaseSession session, DumpMode mode, DumpContents contents, string? snapshotDescription)
    {
        var (tables, objects, loading) = contents;
        var header = new StringBuilder();
        void Line(string text) => header.Append("-- ").Append(text.Replace("\r", "").Replace("\n", " ")).Append('\n');
        void List(string title, IEnumerable<string> items)
        {
            var distinct = items.Distinct().ToList();
            if (distinct.Count == 0) return;
            Line(title + ":");
            foreach (var item in distinct) Line("  " + item);
        }

        Line($"DbConnect SQL dump ({mode.Title().ToLowerInvariant()})");
        Line($"Provider: {session.ProviderName}");
        Line($"Server: {ServerLabel(session)}");
        Line($"Database: {session.DatabaseLabel ?? session.Name}");
        Line($"Created: {DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)}");
        var withData = mode == DumpMode.StructureAndData;
        Line($"Includes: {Included(tables, objects)}{(withData ? ", table data" : "")}");
        Line($"Excludes: {(withData ? "" : "table data; ")}{(objects == DumpObjects.None
            ? "views, routines and other objects outside these tables"
            : "sequences, types and other objects that are not tables, views, routines or triggers")}; users, roles and grants");
        if (withData)
            Line(snapshotDescription != null
                ? $"Data: read in one snapshot ({snapshotDescription})"
                : "Data: not a single snapshot; each table was read as it stood when the dump reached it");
        foreach (var line in loading.HeaderLines(session.ProviderName)) Line(line);
        var dumped = new DumpTableNames(tables.Select(t => t.Table).ToList());
        List("Prerequisites (must already exist; not scripted)",
            tables.SelectMany(t => t.Prerequisites).Where(p => !CreatedByDump(p, dumped, objects)).Select(p => p.Text)
                .Concat(objects.Prerequisites));
        // Every snapshot excludes its rows; the Excludes line says whether this dump has them.
        List("Not reproduced by this script", tables.SelectMany(t => t.Exclusions).Where(e => e != "Table data.").Concat(objects.Exclusions));
        return header.ToString();
    }

    private static string Included(IReadOnlyList<TableDefinitionSnapshot> tables, DumpObjects objects)
    {
        static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
        var partKinds = tables.SelectMany(t => t.Parts).Select(p => p.Kind);
        if (objects.Triggers.Count > 0) partKinds = partKinds.Append(TableDefinitionPartKind.Trigger);
        var kinds = partKinds.Where(k => k != TableDefinitionPartKind.Table)
            .Distinct().Order().Select(kind => kind switch
            {
                TableDefinitionPartKind.Sequence => "sequences",
                TableDefinitionPartKind.Constraint => "constraints",
                TableDefinitionPartKind.Index => "indexes",
                TableDefinitionPartKind.Option => "table options",
                TableDefinitionPartKind.Comment => "comments",
                _ => "triggers"
            });
        var programmable = objects.ViewsAndRoutines.GroupBy(o => o.Identity.Kind).OrderBy(g => g.Key)
            .Select(g => Count(g.Count(), DumpObjects.Title(g.Key).ToLowerInvariant()));
        return string.Join(", ", new[] { Count(tables.Count, "table") }.Concat(kinds).Concat(programmable));
    }

    /// <summary>A referenced table or trigger function the dump itself creates is not a prerequisite.</summary>
    private static bool CreatedByDump(TablePrerequisite prerequisite, DumpTableNames dumped, DumpObjects objects) => prerequisite.Object switch
    {
        { Kind: SchemaNodeType.Table } table => dumped.Find(table) != null,
        { Kind: SchemaNodeType.Function } function => objects.CreatesFunction(function),
        _ => false
    };

    /// <summary>Host and port from the connection string, never credentials; embedded databases have no server.</summary>
    private static string ServerLabel(DatabaseSession session)
    {
        if (session.Capabilities.IsEmbedded) return "embedded (local file)";
        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = session.ConnectionString };
            foreach (var key in new[] { "Host", "Server", "Data Source", "Address" })
                if (builder.TryGetValue(key, out var host) && Convert.ToString(host, CultureInfo.InvariantCulture) is { Length: > 0 } text)
                    return builder.TryGetValue("Port", out var port) && Convert.ToString(port, CultureInfo.InvariantCulture) is { Length: > 0 } p
                        ? $"{text}:{p}" : text;
        }
        catch (ArgumentException) { /* Unparseable connection strings name no server. */ }
        return "unknown";
    }
}
