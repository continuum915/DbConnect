using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class ExportOperationViewModel : ViewModelBase, IProgress<ExportProgress>, IProgress<DumpProgress>
{
    private CancellationTokenSource? _cancellation;
    private string _dumpLabel = string.Empty;
    private int _dumpTables;
    private int _dataTable;
    private long _rowsBeforeTable;
    private long _tableRows;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private long _rowsWritten;
    [ObservableProperty] private ExportOutcome? _outcome;
    [ObservableProperty] private string _message = string.Empty;
    public IExportFileOperations? FileOperations { get; set; }

    public void Declined(ExportScope scope, ExportFormat format)
    {
        Outcome = new(ExportStatus.Canceled, scope, format, 0, TimeSpan.Zero);
        RowsWritten = 0;
        Message = $"Canceled · {scope} · {format} · rerun was not confirmed";
    }

    public void Unavailable(ExportScope scope, ExportFormat format, string reason)
    {
        Outcome = new(ExportStatus.Failed, scope, format, 0, TimeSpan.Zero, Diagnostic: reason);
        RowsWritten = 0;
        Message = $"Failed · {scope} · {format} · {reason}";
    }

    public async Task RunAsync(IExportPathPicker picker, IExportSource source, ExportOptions options, ExportScope scope, string identity)
    {
        if (IsRunning) return;
        IsRunning = true;
        Outcome = null;
        RowsWritten = 0;
        Message = $"{scope} · {options.Format} · preparing export";
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        try
        {
            Outcome = await new ResultExporter(picker, FileOperations).ExportAsync(source, options, scope, identity, this, cancellation.Token);
            RowsWritten = Outcome.RowsWritten;
            Message = $"{Outcome.Status} · {Outcome.Scope} · {Outcome.Format} · {Outcome.RowsWritten} rows · {Outcome.Elapsed.TotalSeconds:F1}s"
                      + Details(Outcome);
        }
        finally { _cancellation = null; IsRunning = false; }
    }

    /// <summary>Dump SQL File to a chosen path, with table progress and Cancel; nothing is published unless it completes.</summary>
    public async Task RunDumpAsync(DatabaseSession session, DumpSelection selection, DumpMode mode, string path)
    {
        if (IsRunning) return;
        IsRunning = true;
        Outcome = null;
        RowsWritten = 0;
        _dumpTables = _dataTable = 0;
        _rowsBeforeTable = _tableRows = 0;
        _dumpLabel = $"Dump SQL File ({mode.Title()})";
        Message = $"{_dumpLabel} · reading the table list";
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        try
        {
            Outcome = await DatabaseDumpService.DumpToFileAsync(session, selection, mode, path, FileOperations, this, cancellation.Token);
            RowsWritten = Outcome.RowsWritten;
            var tables = Outcome.Status != ExportStatus.Completed ? ""
                : $" · {_dumpTables} table{(_dumpTables == 1 ? "" : "s")}" + (mode == DumpMode.StructureAndData ? $" · {RowsWritten} rows" : "");
            Message = $"{Outcome.Status} · {_dumpLabel}{tables} · {Outcome.Elapsed.TotalSeconds:F1}s" + Details(Outcome);
        }
        finally { _cancellation = null; IsRunning = false; }
    }

    private static string Details(ExportOutcome outcome) =>
        (outcome.FinalPath != null ? $" · {outcome.FinalPath}" : "") +
        (outcome.Diagnostic != null ? $" · {outcome.Diagnostic}" : "") +
        (outcome.RetainedTemporaryPath != null ? $" · Retained temporary file: {outcome.RetainedTemporaryPath}" : "");

    void IProgress<DumpProgress>.Report(DumpProgress value)
    {
        _dumpTables = value.TableCount;
        if (value.Rows is not { } rows)
        {
            Message = $"{_dumpLabel} · table {value.TablesRead} of {value.TableCount} · {value.Table}";
            return;
        }
        // Rows count per table; the total adds the tables already finished.
        if (value.TablesRead != _dataTable)
        {
            _rowsBeforeTable += _tableRows;
            _dataTable = value.TablesRead;
        }
        _tableRows = rows;
        RowsWritten = _rowsBeforeTable + rows;
        Message = $"{_dumpLabel} · data for table {value.TablesRead} of {value.TableCount} · {value.Table} · {rows} rows";
    }

    void IProgress<ExportProgress>.Report(ExportProgress value)
    {
        RowsWritten = value.RowsWritten;
        Message = $"Writing · {value.RowsWritten} rows · {value.Elapsed.TotalSeconds:F1}s";
    }

    [RelayCommand] private void Cancel() => _cancellation?.Cancel();
}
