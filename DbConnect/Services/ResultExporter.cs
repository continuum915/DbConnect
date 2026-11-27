using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Shared export workflow. Publication occurs only after normal source completion and file closure.</summary>
public sealed class ResultExporter(IExportPathPicker picker, IExportFileOperations? fileOperations = null)
{
    private readonly IExportFileOperations _files = fileOperations ?? new LocalExportFileOperations();
    internal static ExportFileType FileTypeFor(ExportFormat format) => format switch
    {
        ExportFormat.Json => new("json", "JSON Files", "application/json"),
        ExportFormat.Excel => new("xlsx", "Excel Files", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
        ExportFormat.JsonLines => new("jsonl", "JSON Lines Files", "application/jsonl"),
        ExportFormat.Markdown => new("md", "Markdown Files", "text/markdown"),
        ExportFormat.SqlInsert => new("sql", "SQL Files", "application/sql"),
        _ => new("csv", "CSV Files", "text/csv")
    };

    /// <summary>Writes every row to the stream, reporting each through the callback; returns a diagnostic to surface, if any.</summary>
    private delegate Task<string?> ExportWriter(Stream stream, IExportSource source, IReadOnlyList<ExportColumn> columns,
        ExportOptions options, Action rowWritten, CancellationToken cancellationToken);

    private static ExportWriter WriterFor(ExportFormat format) => format switch
    {
        ExportFormat.Csv => async (stream, source, columns, options, row, token) =>
        {
            await CsvExportWriter.WriteAsync(stream, source, columns, options, row, token);
            return null;
        }
        ,
        ExportFormat.Json or ExportFormat.JsonLines => async (stream, source, columns, options, row, token) =>
        {
            await JsonExportWriter.WriteAsync(stream, source, columns, options.Format == ExportFormat.JsonLines, row, token);
            return null;
        }
        ,
        ExportFormat.Markdown => async (stream, source, columns, options, row, token) =>
        {
            await MarkdownExportWriter.WriteAsync(stream, source, columns, options, row, token);
            return null;
        }
        ,
        ExportFormat.Excel => (stream, source, columns, options, row, token) =>
            ExcelExportWriter.WriteAsync(stream, source, columns, options.IncludeHeaders, row, token),
        ExportFormat.SqlInsert => async (stream, source, columns, options, row, token) =>
        {
            await SqlInsertExportWriter.WriteAsync(stream, source, columns, options, row, token);
            return null;
        }
        ,
        _ => throw new NotSupportedException("Unsupported export format.")
    };

    public async Task<ExportOutcome> ExportAsync(IExportSource source, ExportOptions options, ExportScope scope,
        string? sourceIdentity = null, IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var captured = options.Copy();
        var columns = source.Columns.ToArray();
        var clock = Stopwatch.StartNew();
        long rows = 0;
        OwnedExportFile? file = null;
        string? final = null;
        string? diagnostic = null;
        var status = ExportStatus.Failed;
        try
        {
            var type = FileTypeFor(captured.Format);
            var path = await picker.PickSavePathAsync($"export.{type.Extension}", type);
            cancellationToken.ThrowIfCancellationRequested();
            if (path == null) return new(ExportStatus.Canceled, scope, captured.Format, 0, clock.Elapsed) { SourceIdentity = sourceIdentity };
            file = new OwnedExportFile(_files, path, type.Extension);
            await file.WriteAndPublishAsync(async stream => diagnostic = await WriterFor(captured.Format)(stream, source, columns,
                captured, () => { rows++; progress?.Report(new(rows, clock.Elapsed)); }, cancellationToken), cancellationToken);
            final = file.Destination;
            status = ExportStatus.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { status = ExportStatus.Canceled; }
        catch (JsonExportValueException error) { diagnostic = error.Message + " Destination was not published."; }
        catch (ExcelExportException error) { diagnostic = error.Message + " Destination was not published."; }
        catch (SqlInsertExportException error) { diagnostic = error.Message + " Destination was not published."; }
        catch (Exception error) { diagnostic = $"Export failed ({error.GetType().Name}); destination was not published."; }
        return new(status, scope, captured.Format, rows, clock.Elapsed, final, diagnostic, file?.RetainedTemporaryPath) { SourceIdentity = sourceIdentity };
    }

}
