using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class ExportTableChoice(string name, DatabaseObjectIdentity identity, bool isSelected) : ObservableObject
{
    public string Name { get; } = name;
    public DatabaseObjectIdentity Identity { get; } = identity;
    [ObservableProperty] private bool _isSelected = isSelected;
    [ObservableProperty] private string _result = string.Empty;
}

/// <summary>
/// Export Data from the explorer, like Navicat's Export Wizard: tick tables, pick a format and a folder, and
/// every row of each table streams to its own file (schema.table.ext). The grid row cap does not apply.
/// A failed table is reported and the rest continue; Cancel stops the current table and the remaining ones.
/// SQL INSERT can instead write every ticked table into one combined file, parents first
/// (<see cref="CombinedSqlInsertExport"/>), which is published only when every table was written.
/// </summary>
public sealed partial class ExportDataViewModel(DatabaseSession session, IReadOnlyList<ExportTableChoice> tables) : ViewModelBase, ITableChoices
{
    private CancellationTokenSource? _cancellation;

    public ObservableCollection<ExportTableChoice> Tables { get; } = new(tables);
    public static IReadOnlyList<ExportFormat> Formats { get; } = Enum.GetValues<ExportFormat>();
    public ExportOptions Options { get; } = new();
    public ExportOperationViewModel Operation { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WritesCombinedFile), nameof(TablesCaption))]
    private ExportFormat _format = ExportFormat.Csv;
    partial void OnFormatChanged(ExportFormat value) => Options.Format = value;

    /// <summary>SQL INSERT into one file for every ticked table instead of one file per table.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WritesCombinedFile), nameof(TablesCaption))]
    private bool _combinedFile;

    public bool WritesCombinedFile => CombinedFile && Format == ExportFormat.SqlInsert;

    /// <summary>The combined file's name in the folder: the database's, as Dump SQL File names a whole-database dump.</summary>
    public string CombinedFileName => Sanitize((session.Server != null ? session.DatabaseLabel : null) is { Length: > 0 } database
        ? database : session.Name) + "." + DatabaseDumpService.FileType.Extension;

    public string TablesCaption => WritesCombinedFile
        ? "Tables to export (one combined file, referenced tables first):"
        : "Tables to export (one file per table, every row):";
    [ObservableProperty][NotifyCanExecuteChangedFor(nameof(ExportCommand))] private string _folder = string.Empty;
    [ObservableProperty][NotifyCanExecuteChangedFor(nameof(ExportCommand))] private bool _isRunning;
    [ObservableProperty] private string _summary = string.Empty;

    /// <summary>Asks for the destination folder; null when cancelled.</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (PickFolder != null && await PickFolder() is { } folder) Folder = folder;
    }

    [RelayCommand] private void SelectAll() { foreach (var t in Tables) t.IsSelected = true; }
    [RelayCommand] private void SelectNone() { foreach (var t in Tables) t.IsSelected = false; }

    private bool CanExport() => !IsRunning && Directory.Exists(Folder);

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        IsRunning = true;
        var selected = Tables.Where(t => t.IsSelected).ToList();
        var done = 0;
        var failed = 0;
        foreach (var table in Tables) table.Result = string.Empty;
        try
        {
            if (WritesCombinedFile)
            {
                await ExportCombinedAsync(selected, cancellation.Token);
                return;
            }
            foreach (var table in selected)
            {
                if (cancellation.IsCancellationRequested)
                {
                    table.Result = "Not exported (canceled)";
                    continue;
                }
                table.Result = "Exporting…";
                var outcome = await ExportTableAsync(table, cancellation.Token);
                table.Result = outcome.Status == ExportStatus.Completed
                    ? $"{outcome.RowsWritten} rows → {Path.GetFileName(outcome.FinalPath)}"
                    : $"{outcome.Status}{(outcome.Diagnostic == null ? "" : ": " + outcome.Diagnostic)}";
                if (outcome.Status == ExportStatus.Completed) done++;
                else failed++;
            }
        }
        finally
        {
            _cancellation = null;
            IsRunning = false;
        }
        Summary = $"Exported {done} of {selected.Count} tables" + (failed > 0 ? $"; {failed} failed or canceled." : ".");
    }

    private async Task ExportCombinedAsync(IReadOnlyList<ExportTableChoice> selected, CancellationToken cancellationToken)
    {
        foreach (var table in selected) table.Result = "Waiting…";
        var result = await CombinedSqlInsertExport.ExportAsync(session, selected.Select(t => t.Identity).ToList(), Options,
            Path.Combine(Folder, CombinedFileName), Operation.FileOperations,
            (index, rows) =>
            {
                selected[index].Result = $"Exporting… {rows} rows";
                Operation.Message = $"SQL INSERT · one combined file · {selected[index].Name} · {rows} rows";
            }, cancellationToken);
        var outcome = result.Outcome;
        for (var i = 0; i < selected.Count; i++)
            selected[i].Result = result.Rows[i] is { } rows ? $"{rows} rows"
                : i == result.FailedTable ? $"Failed: {outcome.Diagnostic}"
                : $"Not exported ({outcome.Status.ToString().ToLowerInvariant()})";
        Summary = outcome.Status == ExportStatus.Completed
            ? $"Exported {selected.Count} table{(selected.Count == 1 ? "" : "s")} · {outcome.RowsWritten} rows → {CombinedFileName}"
            : $"{outcome.Status}{(outcome.Diagnostic == null ? "" : ": " + outcome.Diagnostic)}" +
              (outcome.RetainedTemporaryPath != null ? $" Retained temporary file: {outcome.RetainedTemporaryPath}" : "");
    }

    [RelayCommand]
    private void Cancel()
    {
        _cancellation?.Cancel();
        Operation.CancelCommand.Execute(null);
    }

    public static string FileName(ExportTableChoice table, ExportFormat format) =>
        $"{Sanitize(table.Identity.Schema is { } schema ? $"{schema}.{table.Name}" : table.Name)}.{ResultExporter.FileTypeFor(format).Extension}";

    private static string Sanitize(string stem)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars()) stem = stem.Replace(invalid, '_');
        return stem;
    }

    private async Task<ExportOutcome> ExportTableAsync(ExportTableChoice table, CancellationToken cancellationToken)
    {
        IDbSession? reserved = null;
        try
        {
            reserved = new ExportDbSession(await SessionSetup.OpenAsync(session, cancellationToken));
            var source = await TableExportSources.ForTableAsync(session, table.Identity, reserved);
            var options = TableExportSources.OptionsFor(Options, Format, session, table.Identity);
            using var registration = cancellationToken.Register(() => Operation.CancelCommand.Execute(null));
            await Operation.RunAsync(new FolderPicker(Path.Combine(Folder, FileName(table, Format))), source, options,
                ExportScope.AllMatchingTableRows, $"{session.Connection.Id}:{table.Identity}");
            return Operation.Outcome ?? new(ExportStatus.Failed, ExportScope.AllMatchingTableRows, Format, 0, TimeSpan.Zero,
                Diagnostic: "No outcome.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(ExportStatus.Failed, ExportScope.AllMatchingTableRows, Format, 0, TimeSpan.Zero,
                Diagnostic: DiagnosticRedactor.Redact(ex.Message));
        }
        finally
        {
            if (reserved != null) await reserved.DisposeAsync();
        }
    }

    private sealed class FolderPicker(string path) : IExportPathPicker
    {
        public Task<string?> PickSavePathAsync(string suggestedFileName, ExportFileType fileType) => Task.FromResult<string?>(path);
    }
}
