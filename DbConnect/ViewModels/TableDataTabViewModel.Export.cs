using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class TableDataTabViewModel
{
    private IReadOnlyList<ExportColumn> _loadedExportColumns = [];
    // ── Export ────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private ExportOptions _exportOptions = new();

    [ObservableProperty]
    private bool _showExportPanel;

    /// <summary>Where exports are saved; the view supplies a save-dialog picker, tests a stand-in.</summary>
    public IExportPathPicker? ExportPathPicker { get; set; }

    public static IReadOnlyList<ExportFormat> ExportFormatOptions { get; } = Enum.GetValues<ExportFormat>();
    public static IReadOnlyList<CsvDelimiter> DelimiterOptions { get; } = Enum.GetValues<CsvDelimiter>();
    public static IReadOnlyList<NullRepresentation> NullRepOptions { get; } = Enum.GetValues<NullRepresentation>();

    [RelayCommand]
    private void ToggleExportPanel() => ShowExportPanel = !ShowExportPanel;

    public ExportOperationViewModel ExportState { get; } = new();
    public ExportScope ExportScope
    {
        get => ExportOptions.AllPages ? ExportScope.AllMatchingTableRows : ExportScope.CurrentTablePage;
        set { ExportOptions.AllPages = value == ExportScope.AllMatchingTableRows; OnPropertyChanged(); }
    }
    public static IReadOnlyList<ExportScope> ExportScopes { get; } = [ExportScope.CurrentTablePage, ExportScope.AllMatchingTableRows];
    public string ExportScopeExplanation => "Current page includes unsaved edits. All matching rows reads the database using captured server filters and browsing order, excluding unsaved grid edits. Concurrent changes may cause missing or repeated rows; consistency depends on the provider and transaction isolation, with no guaranteed snapshot. All matching rows supports CSV, JSON, JSON Lines, Markdown, Excel and SQL INSERT. Empty null fields cannot be distinguished from empty text.";

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (ExportPathPicker == null || ExportState.IsRunning || IsLoading || IsCommitting) return;
        if (_session != null && !await ObjectSelection.ResolveAsync(_session))
        {
            StatusMessage = "Select this table again in the explorer before exporting: its saved name is ambiguous or unavailable.";
            return;
        }
        var options = ExportOptions.Copy();
        var scope = ExportScope;
        if (_session != null)
        {
            options.SqlDialect = _session.Capabilities;
            var session = _session;
            var identity = ObjectSelection.Identity;
            var sourceTarget = identity is null ? SqlInsertTarget.Quote(TableObjectName, options.SqlDialect) : SqlInsertTarget.ForTable(identity, options.SqlDialect);
            if (string.IsNullOrWhiteSpace(options.SqlTableName)) options.SqlTableName = sourceTarget;
            // The CREATE script names the source table, so inserts into another table would not load after it.
            if (options.IsSqlInsert && options.IncludeCreateTable && options.SqlTableName.Trim() != sourceTarget)
            {
                ExportState.Unavailable(scope, options.Format, $"Include CREATE TABLE creates {sourceTarget}; clear the target table or untick Include CREATE TABLE.");
                StatusMessage = ExportState.Message;
                return;
            }
            options.CreateTableScript = identity is null
                ? _ => Task.FromResult(TableScriptResult.Fail($"{TableObjectName} has no catalog identity to script."))
                : token => TableScripting.ScriptCreateAsync(session, identity, token);
        }
        var columns = DisplayedColumns.ToArray();
        var metadata = columns.Select((name, ordinal) => _loadedExportColumns.FirstOrDefault(c => c.Name == name) ?? new ExportColumn(name, ordinal)).ToArray();
        IExportSource source = scope == ExportScope.AllMatchingTableRows
            ? new TableExportSource(GetOrOpenDbSessionAsync, TableObjectName, BuildQuerySpec(0), metadata)
            : new LoadedExportSource(metadata, Rows.Select(row => (IReadOnlyList<object?>)columns.Select(column => row.Values.GetValueOrDefault(column)).ToArray()));
        await ExportState.RunAsync(ExportPathPicker, source, options, scope, $"{_session?.Connection.Id}:{_session?.Connection.Name}:{TableObjectName}:{scope}:page:{CurrentPageIndex}");
        StatusMessage = ExportState.Message;
    }
}
