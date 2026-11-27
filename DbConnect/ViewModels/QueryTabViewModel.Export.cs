using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class QueryTabViewModel
{
    public ExportOperationViewModel ExportState { get; } = new();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportScopeExplanation))]
    private ExportScope _exportScope = ExportScope.DisplayedQueryResults;
    public static IReadOnlyList<ExportScope> ExportScopes { get; } = [ExportScope.DisplayedQueryResults, ExportScope.FullQuery];
    public string FullQueryUnavailableReason => CurrentResult != null &&
        (_dbSession as IQueryExportSession)?.CaptureQueryExport(CurrentResult) != null
        ? string.Empty
        : "Full query export is unavailable: select a successful, single supported SELECT result on its original open session. Batches, procedures, write/transaction statements, CTEs, locking queries, unknown functions and non-replayable parameter values are excluded; a changed session context cannot be recovered.";
    public string ExportScopeExplanation => ExportScope == ExportScope.FullQuery
        ? string.IsNullOrEmpty(FullQueryUnavailableReason) ? FullQueryDisclosure : FullQueryUnavailableReason
        : "Displayed results only, including filtering and sorting; the query row cap still applies. Empty null fields cannot be distinguished from empty text.";
    public const string FullQueryDisclosure = "Rerun the selected result's original statement without the grid row cap on its original session and transaction. Data may have changed; this can be costly and provides no guaranteed snapshot. Unsaved result edits and client-only filtering/sorting are excluded. SQL, views and virtual tables cannot in general be proven side-effect-free. Cancellation or reader errors may require an explicit transaction rollback. Confirm the target and environment before rerunning.";
    public Func<string, Task<bool>>? ConfirmFullQueryExport { get; set; }
    public Func<IReadOnlyList<int>>? CaptureExportColumnOrder { get; set; }

    [RelayCommand]
    private void CancelExecution()
    {
        _executionCts?.Cancel();
        ExportState.CancelCommand.Execute(null);
        _ = _dbSession?.CancelAsync();
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (ResultColumns.Count == 0 || ExportPathPicker == null || ExportState.IsRunning || IsExecuting) return;
        ShowExportOptionsPanel = true;
        var options = ExportOptions.Copy();
        options.SqlDialect = SelectedSession?.Capabilities;
        if (string.IsNullOrWhiteSpace(options.SqlTableName) && CurrentResult != null)
            options.SqlTableName = SqlInsertTarget.DefaultFor(CurrentResult.ColumnInfos, options.SqlDialect);
        if (ExportScope == ExportScope.FullQuery)
        {
            var capture = CurrentResult == null ? null : (_dbSession as IQueryExportSession)?.CaptureQueryExport(CurrentResult);
            var picker = ExportPathPicker;
            var identity = $"{SelectedSession?.Connection.Id}:{SelectedSession?.Connection.Name}:result:{capture?.ResultIdentity}";
            var disclosure = $"{SelectedSession?.Connection.Name} · {SelectedSession?.Connection.EnvironmentTier}\n{FullQueryDisclosure}";
            if (capture == null)
            {
                ExportState.Unavailable(ExportScope.FullQuery, options.Format, FullQueryUnavailableReason);
                return;
            }
            IsExecuting = true;
            try
            {
                if (ConfirmFullQueryExport == null || !await ConfirmFullQueryExport(disclosure))
                {
                    ExportState.Declined(ExportScope.FullQuery, options.Format);
                    return;
                }
                if (!capture.IsCurrent())
                {
                    ExportState.Unavailable(ExportScope.FullQuery, options.Format, "Original session context changed; rerun the query first.");
                    return;
                }
                // SQLite may complete ReadAsync synchronously; keep uncapped reading off the UI thread.
                await ExportState.RunAsync(picker, new BackgroundExportSource(capture.Source), options, ExportScope.FullQuery, identity);
            }
            finally { IsExecuting = false; OnPropertyChanged(nameof(ExportScopeExplanation)); }
            return;
        }
        var ordinals = (CaptureExportColumnOrder?.Invoke() ?? ResultGrid.VisibleOrdinals).ToArray();
        var columns = ordinals.Select(i => CurrentResult!.ColumnInfos[i])
            .Select(c => new ExportColumn(c.Name, c.Ordinal, c.ProviderTypeName, c.ClrType));
        var source = new LoadedExportSource(columns, DisplayResultRows.Select(row =>
        {
            var raw = ResultEditor.CurrentValuesOf(row) ?? QueryResultDisplayRow.RawValuesOf(row) ?? row.ToList<object?>();
            return (IReadOnlyList<object?>)ordinals.Select(i => i < raw.Count ? raw[i] : null).ToArray();
        }));
        await ExportState.RunAsync(ExportPathPicker, source, options, ExportScope.DisplayedQueryResults,
            $"{SelectedSession?.Connection.Id}:{SelectedSession?.Connection.Name}:result:{CurrentResult?.ResultIndex}");
    }
}
