using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// A backup made with Dump SQL File (<see cref="BackupStrategy.DbConnectDump"/>): the whole database, structure and data,
/// published atomically. Progress is reported as log lines: each table as its data starts, every
/// <see cref="RowsPerLine"/> rows within it and its row count when it is done, then a summary.
/// </summary>
internal static class DumpBackup
{
    private const long RowsPerLine = 100_000;

    public static async Task RunAsync(DatabaseSession session, string path, IProgress<string> progress, CancellationToken cancellationToken)
    {
        progress.Report($"Backing up {session.Name} with DbConnect's dump to {path}…");
        var tables = new TableLines(progress);
        var outcome = await DatabaseDumpService.DumpToFileAsync(session, DumpSelection.Database, DumpMode.StructureAndData, path,
            progress: tables, cancellationToken: cancellationToken);
        // A temporary file that could not be removed is the only thing a failed or cancelled backup leaves behind.
        var leftOver = outcome.RetainedTemporaryPath is { } temporary ? $" A temporary file could not be removed: {temporary}" : "";
        switch (outcome.Status)
        {
            case ExportStatus.Completed:
                tables.Finish();
                progress.Report(string.Create(CultureInfo.InvariantCulture,
                    $"Backup complete: {outcome.RowsWritten:N0} rows from {tables.Count} tables in {outcome.Elapsed:mm\\:ss}."));
                break;
            case ExportStatus.Canceled:
                throw new OperationCanceledException($"Backup cancelled; nothing was published.{leftOver}", cancellationToken);
            default:
                throw new InvalidOperationException((outcome.Diagnostic ?? "The dump failed.") + leftOver);
        }
    }

    private sealed class TableLines(IProgress<string> progress) : IProgress<DumpProgress>
    {
        private string? _table;
        private long _rows;

        /// <summary>The tables the dump covers.</summary>
        public int Count { get; private set; }

        public void Report(DumpProgress value)
        {
            Count = value.TableCount;
            if (value.Rows is not { } rows) return;
            if (value.Table != _table)
            {
                Finish();
                _table = value.Table;
                progress.Report($"Table {value.TablesRead} of {value.TableCount}: {value.Table}");
            }
            else if (rows > 0 && rows % RowsPerLine == 0)
                progress.Report(string.Create(CultureInfo.InvariantCulture, $"  {value.Table}: {rows:N0} rows so far"));
            _rows = rows;
        }

        /// <summary>Reports the rows of the table whose data was written last.</summary>
        public void Finish()
        {
            if (_table != null) progress.Report(string.Create(CultureInfo.InvariantCulture, $"  {_table}: {_rows:N0} rows"));
            _table = null;
        }
    }
}
