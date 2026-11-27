using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

public static class ExportService
{
    public static async Task ExportExcelAsync(QueryResult result, string filePath)
    {
        var outcome = await new ResultExporter(new FixedPathPicker(filePath)).ExportAsync(
            new LoadedExportSource(result.ColumnInfos.Select(c => new ExportColumn(c.Name, c.Ordinal)), result.ObjectRows),
            new() { Format = ExportFormat.Excel }, ExportScope.DisplayedQueryResults);
        if (outcome.Status != ExportStatus.Completed)
            throw new IOException(outcome.Diagnostic ?? "Excel export failed.");
    }

    public static async Task ExportJsonLinesAsync(QueryResult result, string filePath)
    {
        var outcome = await new ResultExporter(new FixedPathPicker(filePath)).ExportAsync(
            new LoadedExportSource(result.ColumnInfos.Select(c => new ExportColumn(c.Name, c.Ordinal)), result.ObjectRows),
            new() { Format = ExportFormat.JsonLines }, ExportScope.DisplayedQueryResults);
        if (outcome.Status != ExportStatus.Completed)
            throw new IOException(outcome.Diagnostic ?? "JSON Lines export failed.");
    }

    private sealed class FixedPathPicker(string path) : IExportPathPicker
    {
        public Task<string?> PickSavePathAsync(string name, ExportFileType type) => Task.FromResult<string?>(path);
    }

    public static async Task ExportMarkdownAsync(QueryResult result, string filePath)
    {
        var outcome = await new ResultExporter(new FixedPathPicker(filePath)).ExportAsync(
            new LoadedExportSource(result.ColumnInfos.Select(c => new ExportColumn(c.Name, c.Ordinal)), result.ObjectRows),
            new() { Format = ExportFormat.Markdown }, ExportScope.DisplayedQueryResults);
        if (outcome.Status != ExportStatus.Completed)
            throw new IOException(outcome.Diagnostic ?? "Markdown export failed.");
    }
}
