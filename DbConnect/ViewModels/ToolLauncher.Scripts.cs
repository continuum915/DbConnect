using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.Views;

namespace DbConnect.ViewModels;

public partial class ToolLauncher
{
    public async Task OpenDdlTabAsync(SchemaNode node)
    {
        node = node.RealNode ?? node;
        var session = _schemaTree.FindSessionForNode(node);
        if (session is null) return;

        var schema = node.SchemaPath ?? node.SchemaName ?? string.Empty;
        var name = node.RawName ?? node.Name;

        string? ddl = node.Type switch
        {
            SchemaNodeType.Procedure => await session.GetProcedureDefinitionAsync(schema, name),
            SchemaNodeType.Function => await session.GetFunctionDefinitionAsync(schema, name),
            SchemaNodeType.Trigger => await session.GetTriggerDefinitionAsync(schema, name),
            _ => null
        };

        if (string.IsNullOrEmpty(ddl))
            ddl = $"-- Definition not available for {node.Name}";

        var existingTitle = $"{node.Name} (DDL)";
        var existing = _tabs.OfType<QueryTabViewModel>().FirstOrDefault(t => t.Title == existingTitle);
        if (existing != null)
        {
            _selectTab(existing);
            return;
        }

        var tab = new QueryTabViewModel { Title = existingTitle, IsReadOnly = true };
        tab.QueryText = ddl;
        tab.ClearDirty();
        _tabs.Add(tab);
        _selectTab(tab);
    }

    public void ScriptTableAsSelect(SchemaNode node)
    {
        node = node.RealNode ?? node;
        var tableName = node.RawName ?? node.Name;
        var tab = new QueryTabViewModel($"SELECT {tableName}", _sessions, _queryWorkspaceService);

        DatabaseSession? session = _schemaTree.FindSessionForNode(node);
        if (session != null)
        {
            tab.QueryText = session.FormatLimitQuery($"SELECT * FROM {ObjectNames.Quote(session.Capabilities, _schemaTree.GetIdentity(node, session))}", 100);
            tab.ClearDirty();
            tab.SelectedSession = session;
            if (_indexes.TryGetValue(_schemaTree.FindRootNode(node), out var index))
            {
                tab.SetSessionIndex(session, index);
                tab.CurrentIndex = index;
            }
        }

        _tabs.Add(tab);
        _selectTab(tab);
    }

    /// <summary>Script As for an explorer node: generate the text, then send it to a tab, the clipboard or a file.</summary>
    public async Task ScriptObjectAsync(SchemaNode node, ScriptKind kind, ScriptTarget target)
    {
        node = node.RealNode ?? node;
        if (kind == ScriptKind.Select && target == ScriptTarget.NewTab)
        {
            ScriptTableAsSelect(node);
            return;
        }
        var session = _schemaTree.FindSessionForNode(node);
        if (session == null) return;
        var label = $"Script as {kind.ToString().ToUpperInvariant()}";
        var result = await ObjectScriptText.GenerateAsync(session, node, _schemaTree.GetIdentity(node, session), kind);
        if (result.Error is { } error)
        {
            ReportError(label, error);
            return;
        }
        var name = node.RawName ?? node.Name;
        switch (target)
        {
            case ScriptTarget.Clipboard when CopyToClipboardAsync != null:
                await CopyToClipboardAsync(result.Sql!);
                ReportStatus($"{label} for {name} copied to the clipboard.");
                break;
            case ScriptTarget.File when PickScriptPathAsync != null:
                var path = await PickScriptPathAsync($"{name} {kind.ToString().ToLowerInvariant()}.sql");
                if (path == null) return;
                try
                {
                    await SqlFileService.WriteAsync(path, result.Sql!, null);
                    ReportStatus($"{label} for {name} saved to {path}.");
                }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
                {
                    ReportError(label, $"Could not save {path}: {ex.Message}");
                }
                break;
            default:
                var tab = new QueryTabViewModel($"{kind.ToString().ToUpperInvariant()} {name}", _sessions, _queryWorkspaceService);
                tab.QueryText = result.Sql!;
                tab.ClearDirty();
                tab.SelectedSession = session;
                _tabs.Add(tab);
                _selectTab(tab);
                break;
        }
    }

    public Task ScriptObjectAsCreate(SchemaNode node) => ScriptObjectAsync(node, ScriptKind.Create, ScriptTarget.NewTab);

    public Task ScriptObjectAsDrop(SchemaNode node) => ScriptObjectAsync(node, ScriptKind.Drop, ScriptTarget.NewTab);

    public void ScriptPragma(SchemaNode node)
    {
        node = node.RealNode ?? node;
        var session = _schemaTree.FindSessionForNode(node);
        if (session == null) return;

        // RawName is the pragma key; Name is "key = value"
        var pragmaKey = node.RawName ?? node.Name;
        var currentValue = node.Name.Contains('=')
            ? node.Name[(node.Name.IndexOf('=') + 1)..].Trim()
            : "";

        // Suggest a toggle value for boolean-style pragmas
        var toggleSuggestion = pragmaKey.ToLowerInvariant() switch
        {
            "foreign_keys" => currentValue == "1" ? "OFF" : "ON",
            "recursive_triggers" => currentValue == "1" ? "OFF" : "ON",
            "case_sensitive_like" => currentValue == "1" ? "OFF" : "ON",
            "journal_mode" => "WAL",
            "synchronous" => "NORMAL",
            _ => null
        };

        string sql;
        if (toggleSuggestion != null)
            sql = $"-- Current: PRAGMA {pragmaKey} = {currentValue}\nPRAGMA {pragmaKey} = {toggleSuggestion};";
        else
            sql = $"PRAGMA {pragmaKey};";

        var tab = new QueryTabViewModel($"PRAGMA {pragmaKey}", _sessions, _queryWorkspaceService);
        tab.QueryText = sql;
        tab.ClearDirty();
        tab.SelectedSession = session;
        _tabs.Add(tab);
        _selectTab(tab);
    }
}
