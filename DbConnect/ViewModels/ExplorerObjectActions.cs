using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>Table actions in the explorer menu beyond Script As, as Navicat and dbForge offer them.</summary>
public enum ExplorerObjectAction
{
    ExportData,
    Truncate,
    Empty,
    Rename,
    Duplicate,
    CopyDdl,
    DumpSql,
    TransferData
}

/// <summary>
/// Explorer actions that change or copy a table: Drop, Truncate, Empty, Rename, Duplicate, Copy DDL and Export
/// Data. Destructive actions show their exact SQL through the host's confirmation, which asks for the typed name on
/// Production connections; schema changes invalidate the affected catalog scope so the tree refreshes.
/// </summary>
public sealed class ExplorerObjectActions(MainWindowViewModel host)
{
    /// <summary>Asks for a new object name: title, prompt, initial value. Null when cancelled.</summary>
    public Func<string, string, string, Task<string?>>? PromptForName { get; set; }

    public Func<ExportDataViewModel, Task>? ShowExportDialog { get; set; }

    /// <summary>Asks where to save a Dump SQL File.</summary>
    public IExportPathPicker? DumpPathPicker { get; set; }

    /// <summary>Shows a running dump's progress with Cancel; returns when the user closes it.</summary>
    public Func<ExportOperationViewModel, Task>? ShowDumpProgress { get; set; }

    public SchemaActionAvailability Availability(SchemaNode? node, ExplorerObjectAction action)
    {
        node = node?.RealNode ?? node;
        if (node == null) return new(false, false);
        var isTable = node.Type is SchemaNodeType.Table;
        var isView = node.Type is SchemaNodeType.View or SchemaNodeType.MaterializedView;
        var visible = action switch
        {
            ExplorerObjectAction.ExportData or ExplorerObjectAction.Rename => isTable || isView,
            ExplorerObjectAction.CopyDdl => ObjectScriptText.KindsFor(node.Type).Contains(ScriptKind.Create),
            ExplorerObjectAction.DumpSql or ExplorerObjectAction.TransferData => isTable || node.Type is SchemaNodeType.Database or SchemaNodeType.Schema,
            _ => isTable
        };
        var session = host.SchemaTree.FindSessionForNode(node);
        if (!visible || session == null) return new(visible, false, visible ? "Connect before using this action." : null);
        var dialect = TableMaintenanceDialects.For(session.ProviderName);
        string? reason = action switch
        {
            ExplorerObjectAction.Truncate or ExplorerObjectAction.Empty or ExplorerObjectAction.Rename
                or ExplorerObjectAction.Duplicate when session.Connection.Options?.IsReadOnly == true =>
                $"{session.Name} is a read-only connection.",
            ExplorerObjectAction.Truncate when dialect?.Truncate(Identity(node, session)) == null =>
                $"{session.ProviderName} has no TRUNCATE statement; use Empty Table.",
            ExplorerObjectAction.Rename when dialect == null => "Renaming is not available for this connection.",
            ExplorerObjectAction.Rename when dialect.Rename(Identity(node, session), "x", out var why) == null => why,
            ExplorerObjectAction.Duplicate when session.Provider is not ITableDuplicateSource =>
                "Duplicating needs complete table scripting, which this connection does not support.",
            ExplorerObjectAction.TransferData when session.Provider is not ITableDesignSource =>
                "Transferring needs table designs, which this connection does not support.",
            ExplorerObjectAction.DumpSql when session.Provider is not ITableScriptSource =>
                "Dumping needs complete table scripting, which this connection does not support.",
            ExplorerObjectAction.Empty or ExplorerObjectAction.Truncate when dialect == null =>
                "This action is not available for this connection.",
            _ => null
        };
        return new(true, reason == null, reason);
    }

    private DatabaseObjectIdentity Identity(SchemaNode node, DatabaseSession session) => host.SchemaTree.GetIdentity(node, session);

    private void Status(string message, bool error = false)
    {
        host.StatusMessage = message;
        host.StatusHasError = error;
        if (error) host.StatusErrorText = message;
    }

    /// <summary>Shows the exact SQL; Production connections also need the typed object name.</summary>
    private async Task<bool> ConfirmAsync(string action, SchemaNode node, DatabaseSession session, string sql)
    {
        var name = node.RawName ?? node.Name;
        var connection = host.ActiveConnections.Values.FirstOrDefault(c => c.Name == session.Name);
        var preview = new DestructiveActionPreview(action, node.Type.ToString(), name, session.Name,
            connection?.EnvironmentTier ?? "Development", sql);
        if (host.ShowDestructiveActionConfirmDialog != null) return await host.ShowDestructiveActionConfirmDialog(preview);
        if (host.ShouldConfirmDestructiveAction(session) && host.ShowProductionConfirmDialog != null)
            return await host.ShowProductionConfirmDialog(name);
        return true;
    }

    public async Task DropAsync(SchemaNode node)
    {
        node = node.RealNode ?? node;
        var type = node.Type.ToString();
        var name = node.RawName ?? node.Name;
        var session = host.SchemaTree.FindSessionForNode(node);
        if (session == null) return;
        if (node.Type == SchemaNodeType.View && !session.Capabilities.SupportsViews)
        {
            Status("⚠️ This provider does not support view operations in the current workflow.", true);
            host.StatusErrorText = "View operations not supported for this provider.";
            return;
        }
        if (session.Connection.Options?.IsReadOnly == true)
        {
            Status($"{session.Name} is a read-only connection; {name} was not dropped.", true);
            return;
        }
        var sql = $"DROP {type.ToUpper()} {DdlGenerator.Quote(name, session.ProviderName)};";
        if (!await ConfirmAsync("Drop", node, session, sql)) return;
        if (host.ShowDropConfirmationWithDependencies != null)
        {
            try
            {
                var deps = await new DependencyAnalysisService().GetDependenciesAsync(session, name, type);
                if (deps.Count > 0 && !await host.ShowDropConfirmationWithDependencies(name, deps)) return;
            }
            catch { /* dependency check must not block drop */ }
        }
        try
        {
            Status($"Dropping {type} {name}...");
            host.StatusQueryText = $"Dropping {type} {name}...";
            await session.ExecuteNonQueryAsync(sql, SchemaChangeScopes.ObjectDetails(session, node));
            Status($"✅ Successfully dropped {type} {name}.");
            host.StatusQueryText = $"Dropped {type} {name}.";
        }
        catch (Exception ex)
        {
            Status($"❌ Failed to drop {type}: {ex.Message}", true);
            host.StatusErrorText = $"Failed to drop {type}: {ex.Message}";
        }
    }

    public Task TruncateAsync(SchemaNode node) => ClearAsync(node, truncate: true);

    public Task EmptyAsync(SchemaNode node) => ClearAsync(node, truncate: false);

    private async Task ClearAsync(SchemaNode node, bool truncate)
    {
        node = node.RealNode ?? node;
        var action = truncate ? ExplorerObjectAction.Truncate : ExplorerObjectAction.Empty;
        if (!Availability(node, action).IsEnabled) return;
        var session = host.SchemaTree.FindSessionForNode(node)!;
        var dialect = TableMaintenanceDialects.For(session.ProviderName)!;
        var identity = Identity(node, session);
        var sql = truncate ? dialect.Truncate(identity)! : dialect.Empty(identity);
        var label = truncate ? "Truncate" : "Empty";
        var shown = truncate && dialect.TruncateNote is { } note ? $"-- {note}\n{sql}" : sql;
        if (!await ConfirmAsync(label, node, session, shown)) return;
        try
        {
            await session.ExecuteNonQueryAsync(sql, SchemaChangeScopes.ObjectDetails(session, node));
            Status($"✅ {label} {identity.Name} completed.");
        }
        catch (Exception ex)
        {
            Status($"❌ {label} {identity.Name} failed: {ex.Message}" +
                   (truncate ? " Empty Table deletes the rows instead." : ""), true);
        }
    }

    public async Task RenameAsync(SchemaNode node)
    {
        node = node.RealNode ?? node;
        if (!Availability(node, ExplorerObjectAction.Rename).IsEnabled || PromptForName == null) return;
        var session = host.SchemaTree.FindSessionForNode(node)!;
        var identity = Identity(node, session);
        var newName = (await PromptForName($"Rename {node.Type}", $"New name for {identity.Name}:", identity.Name))?.Trim();
        if (string.IsNullOrEmpty(newName) || newName == identity.Name) return;
        var sql = TableMaintenanceDialects.For(session.ProviderName)!.Rename(identity, newName, out _)!;
        var warning = await DependentsWarningAsync(session, identity.Name, node.Type.ToString());
        if (!await ConfirmAsync("Rename", node, session, warning + sql)) return;
        try
        {
            await session.ExecuteNonQueryAsync(sql, SchemaChangeScopes.Membership(session, node));
            Status($"✅ Renamed {identity.Name} to {newName}.");
        }
        catch (Exception ex)
        {
            Status($"❌ Rename failed: {ex.Message}", true);
        }
    }

    /// <summary>Dependents are listed, not rewritten: views, routines and foreign keys may break.</summary>
    private static async Task<string> DependentsWarningAsync(DatabaseSession session, string name, string type)
    {
        try
        {
            var deps = await new DependencyAnalysisService().GetDependenciesAsync(session, name, type);
            if (deps.Count == 0) return "";
            return "-- These objects refer to the old name and may stop working:\n" +
                   string.Concat(deps.Take(20).Select(d => $"--   {d.ObjectType} {d.ObjectName}\n"));
        }
        catch { return ""; }
    }

    public async Task DuplicateAsync(SchemaNode node, bool includeData)
    {
        node = node.RealNode ?? node;
        if (!Availability(node, ExplorerObjectAction.Duplicate).IsEnabled) return;
        var session = host.SchemaTree.FindSessionForNode(node)!;
        var identity = Identity(node, session);
        var newName = CopyName(node, identity.Name);
        try
        {
            var plan = await ((ITableDuplicateSource)session.Provider).PlanDuplicateAsync(
                session.ConnectionString, identity, newName, includeData);
            var result = await session.ExecuteTransactionAsync(plan.Statements);
            await session.SchemaChanges.InvalidateAsync(SchemaChangeScopes.Membership(session, node));
            if (!result.Succeeded)
            {
                Status($"❌ Duplicate {identity.Name} failed ({result.Outcome}): {result.DiagnosticMessage}", true);
                return;
            }
            var notCopied = plan.NotCopied.Count == 0 ? "" : " Not copied: " + string.Join(" ", plan.NotCopied);
            Status($"✅ Duplicated {identity.Name} as {newName}{(includeData ? " with data" : "")}.{notCopied}");
        }
        catch (TableDefinitionUnavailableException ex)
        {
            Status($"❌ {ex.Message}", true);
        }
        catch (Exception ex)
        {
            Status($"❌ Duplicate {identity.Name} failed: {ex.Message}", true);
        }
    }

    /// <summary>Navicat names copies name_copy1, name_copy2, ... skipping names already in the explorer.</summary>
    private string CopyName(SchemaNode node, string name)
    {
        var parent = Parent(node);
        var taken = (parent?.Children ?? []).Select(c => c.RawName ?? c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; ; i++)
            if (!taken.Contains($"{name}_copy{i}")) return $"{name}_copy{i}";
    }

    private SchemaNode? Parent(SchemaNode node) =>
        host.ActiveSessions.Keys.Select(root => SchemaTreeViewModel.FindParentOf(root, node)).FirstOrDefault(p => p != null);

    /// <summary>
    /// Transfer Data… opens the Data Transfer wizard with this connection or database as the source: on a table, that
    /// table and the other selected tables of its connection are ticked; on a schema, its tables; on a database, all.
    /// </summary>
    public void TransferData(SchemaNode node, IReadOnlyList<SchemaNode> selected)
    {
        node = node.RealNode ?? node;
        if (!Availability(node, ExplorerObjectAction.TransferData).IsEnabled) return;
        var session = host.SchemaTree.FindSessionForNode(node)!;
        Func<DatabaseObjectIdentity, bool> tick = node.Type switch
        {
            // By name: the explorer's identities and the catalog's listing differ in connection bookkeeping.
            SchemaNodeType.Table => SelectedTables(node, session, selected).Select(t => ObjectNames.SchemaQualified(Identity(t, session)))
                .ToHashSet(StringComparer.Ordinal) is var names ? t => names.Contains(ObjectNames.SchemaQualified(t)) : _ => false,
            SchemaNodeType.Schema => t => t.Schema == (node.RawName ?? node.Name),
            _ => _ => true
        };
        host.Tools.OpenDataTransferFrom(session, tick);
    }

    public Task CopyDdlAsync(SchemaNode node) => host.Tools.ScriptObjectAsync(node, ScriptKind.Create, ScriptTarget.Clipboard);

    public async Task ExportDataAsync(SchemaNode node)
    {
        node = node.RealNode ?? node;
        if (!Availability(node, ExplorerObjectAction.ExportData).IsEnabled || ShowExportDialog == null) return;
        var session = host.SchemaTree.FindSessionForNode(node)!;
        var parent = Parent(node);
        var candidates = (parent?.Children ?? [])
            .Where(c => c.Type is SchemaNodeType.Table or SchemaNodeType.View or SchemaNodeType.MaterializedView)
            .DefaultIfEmpty(node)
            .Select(c => new ExportTableChoice(c.RawName ?? c.Name, Identity(c, session), ReferenceEquals(c, node)))
            .ToList();
        if (!candidates.Any(c => c.IsSelected)) candidates.Insert(0, new(node.RawName ?? node.Name, Identity(node, session), true));
        await ShowExportDialog(new ExportDataViewModel(session, candidates));
    }

    /// <summary>The selected tables of this table's connection, this one first; for any other node, none.</summary>
    private List<SchemaNode> SelectedTables(SchemaNode node, DatabaseSession session, IReadOnlyList<SchemaNode>? selected) =>
        node.Type != SchemaNodeType.Table || selected == null ? []
            : selected.Select(n => n.RealNode ?? n).Prepend(node)
                .Where(n => n.Type == SchemaNodeType.Table && host.SchemaTree.FindSessionForNode(n) == session).Distinct().ToList();

    /// <summary>The tables beside this one in the explorer, the selected ones ticked; selected tables elsewhere follow.</summary>
    private List<ExportTableChoice> TableChoices(SchemaNode node, DatabaseSession session, IReadOnlyList<SchemaNode> selected)
    {
        var siblings = (Parent(node)?.Children ?? []).Where(c => c.Type == SchemaNodeType.Table).ToList();
        // Tables from elsewhere are named with their schema, so same-named tables can be told apart.
        return siblings.Select(t => new ExportTableChoice(t.RawName ?? t.Name, Identity(t, session), selected.Contains(t)))
            .Concat(selected.Where(t => !siblings.Contains(t))
                .Select(t => Identity(t, session)).Select(id => new ExportTableChoice(ObjectNames.SchemaQualified(id), id, true)))
            .ToList();
    }

    /// <summary>Asks which of several selected tables to dump, and how; the dump goes ahead only if it was confirmed.</summary>
    public Func<DumpTablesViewModel, Task>? ShowDumpTables { get; set; }

    /// <summary>
    /// Dump SQL File on a connection or database (every table), a schema (its tables) or a table. With several tables
    /// selected in the explorer, the table list opens with them ticked beside their siblings, to choose the tables and
    /// the mode. The save dialog comes next; the dump then runs with progress and Cancel, and its outcome lands in the
    /// status bar.
    /// </summary>
    public async Task DumpSqlAsync(SchemaNode node, DumpMode mode, IReadOnlyList<SchemaNode>? selected = null)
    {
        node = node.RealNode ?? node;
        if (!Availability(node, ExplorerObjectAction.DumpSql).IsEnabled || DumpPathPicker == null) return;
        var session = host.SchemaTree.FindSessionForNode(node)!;
        var identity = Identity(node, session);
        // A server connection node dumps its default database, so the file is named after that database.
        string DatabaseName(SchemaNode database) =>
            session.Server != null && session.DatabaseLabel is { Length: > 0 } label ? label : database.RawName ?? database.Name;
        DumpSelection selection;
        string name;
        if (SelectedTables(node, session, selected) is { Count: > 1 } tables)
        {
            if (ShowDumpTables == null) return;
            var choices = new DumpTablesViewModel(TableChoices(node, session, tables), mode);
            await ShowDumpTables(choices);
            if (!choices.Confirmed || choices.SelectedTables.Count == 0) return;
            (selection, mode) = (new DumpSelection { Tables = choices.SelectedTables }, choices.Mode.Mode);
            name = host.ActiveSessions.FirstOrDefault(s => s.Value == session).Key is { } root ? DatabaseName(root) : session.Name;
        }
        else
            (selection, name) = node.Type switch
            {
                SchemaNodeType.Table => (DumpSelection.ForTables(identity), ObjectNames.SchemaQualified(identity)),
                SchemaNodeType.Schema => (DumpSelection.ForSchemas(identity.Name), identity.Name),
                _ => (DumpSelection.Database, DatabaseName(node))
            };
        var path = await DumpPathPicker.PickSavePathAsync($"{name}.{DatabaseDumpService.FileType.Extension}", DatabaseDumpService.FileType);
        if (path == null) return;
        var operation = new ExportOperationViewModel();
        var run = operation.RunDumpAsync(session, selection, mode, path);
        if (ShowDumpProgress != null) await ShowDumpProgress(operation);
        await run;
        Status(operation.Message, operation.Outcome?.Status == ExportStatus.Failed);
    }
}
