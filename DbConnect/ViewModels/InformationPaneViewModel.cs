using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>A label and value in the Information pane's General list.</summary>
public sealed record InformationItem(string Label, string Value);

/// <summary>
/// The right-hand Information pane (the design's Main artboard): the object selected in the Objects tab, or else in the
/// navigator, with its General metadata ("—" when unknown), its DDL from the scripting service, and Usage (not built
/// yet). It replaces the sidebar's Properties section; everything Properties showed is in General.
/// </summary>
public sealed partial class InformationPaneViewModel : ObservableObject
{
    private readonly MainWindowViewModel _host;
    private readonly DocumentStrip _strip;
    private int _ddlRequest;

    [ObservableProperty]
    private bool _hasSubject;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private Symbol _icon = Symbol.Table;

    [ObservableProperty]
    private string _iconRole = "IconBlue";

    [ObservableProperty]
    private string _ddl = string.Empty;

    [ObservableProperty]
    private string _ddlStatus = string.Empty;

    public InformationPaneViewModel(MainWindowViewModel host, DocumentStrip strip)
    {
        _host = host;
        _strip = strip;
        host.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.SelectedSchemaNode)) Refresh();
        };
        strip.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DocumentStrip.IsObjectsSelected)) Refresh();
        };
        strip.Objects.PropertyChanged += OnObjectsChanged;
        Refresh();
    }

    /// <summary>Whether the pane is shown; kept with the window layout.</summary>
    public SidebarState Layout => _host.Sidebar;

    public ObservableCollection<InformationItem> General { get; } = [];

    [RelayCommand]
    private void Close() => Layout.InformationPaneVisible = false;

    [RelayCommand]
    private void Toggle() => Layout.InformationPaneVisible = !Layout.InformationPaneVisible;

    private void OnObjectsChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A new selection, or the list reloaded with statistics.
        if (e.PropertyName is nameof(ObjectsTabViewModel.SelectedRow) or nameof(ObjectsTabViewModel.FooterText)) Refresh();
    }

    /// <summary>Shows the Objects tab's selected row while that tab is in front, else the navigator's selection.</summary>
    public void Refresh()
    {
        var row = _strip.IsObjectsSelected ? _strip.Objects.SelectedRow : null;
        if (row == null && _host.SelectedSchemaNode is { } selected)
            row = _strip.Objects.RowFor(selected.RealNode ?? selected);
        var node = row?.Node ?? (_host.SelectedSchemaNode is { } chosen ? chosen.RealNode ?? chosen : null);
        General.Clear();
        Ddl = string.Empty;
        DdlStatus = string.Empty;
        HasSubject = row != null || node != null;
        if (!HasSubject) return;

        if (row is { Sql: { } sql })
        {
            Name = row.Name;
            Subtitle = "Saved query";
            Icon = Symbol.DocumentText;
            IconRole = "Accent";
            General.Add(new InformationItem("Type", "Saved query"));
            Ddl = sql;
            return;
        }

        var session = _host.GetSessionForNode(node!);
        Name = node!.RawName ?? node.Name;
        Subtitle = node.SchemaName is { Length: > 0 } schema ? $"{TypeLabel(node.Type)} · {schema}" : TypeLabel(node.Type);
        Icon = node.FluentIconSymbol ?? Symbol.Info;
        IconRole = node.IconRole;

        General.Add(new InformationItem("Type", TypeLabel(node.Type)));
        General.Add(new InformationItem("Provider", session?.ProviderName ?? ObjectRow.Unknown));
        General.Add(new InformationItem("Qualified name", node.SchemaName != null ? $"{node.SchemaName}.{Name}" : Name));
        if (!string.IsNullOrEmpty(node.DataType)) General.Add(new InformationItem("Data type", node.DataType));
        if (node.Type is SchemaNodeType.Table or SchemaNodeType.VirtualTable)
        {
            var stats = row ?? new ObjectRow(Name, Icon, IconRole) { Node = node };
            General.Add(new InformationItem("Rows", stats.Rows));
            General.Add(new InformationItem("Engine", stats.Engine));
            General.Add(new InformationItem("Auto increment", stats.AutoIncrement));
            General.Add(new InformationItem("Data length", stats.DataLength));
            General.Add(new InformationItem("Index length", stats.IndexLength));
            General.Add(new InformationItem("Created", stats.Created));
            General.Add(new InformationItem("Modified", stats.Modified));
            General.Add(new InformationItem("Collation", stats.Collation));
            General.Add(new InformationItem("Comment", stats.Comment));
        }
        else if (!string.IsNullOrWhiteSpace(node.Description))
        {
            General.Add(new InformationItem("Comment", node.Description));
        }

        _ = LoadDdlAsync(node, session);
    }

    private async Task LoadDdlAsync(SchemaNode node, DatabaseSession? session)
    {
        var request = ++_ddlRequest;
        if (session == null || !ObjectScriptText.KindsFor(node.Type).Contains(ScriptKind.Create))
        {
            DdlStatus = "No DDL for this object.";
            return;
        }

        DdlStatus = "Loading…";
        var identity = _host.SchemaTree.GetIdentity(node, session);
        var result = await ObjectScriptText.GenerateAsync(session, node, identity, ScriptKind.Create);
        if (request != _ddlRequest) return;
        Ddl = result.Sql ?? string.Empty;
        DdlStatus = result.Sql == null ? result.Error ?? "The DDL could not be read." : string.Empty;
    }

    private static string TypeLabel(SchemaNodeType type) => type switch
    {
        SchemaNodeType.Database => "Database",
        SchemaNodeType.VirtualTable => "Virtual table",
        SchemaNodeType.MaterializedView => "Materialized view",
        SchemaNodeType.ConnectionGroup => "Connection group",
        _ => System.Text.RegularExpressions.Regex.Replace(type.ToString(), "(?<=[a-z])([A-Z])", " $1").ToLowerInvariant() is var words
            ? char.ToUpperInvariant(words[0]) + words[1..] : type.ToString(),
    };
}
