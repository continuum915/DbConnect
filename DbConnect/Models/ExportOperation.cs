using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace DbConnect.Models;

public enum ExportScope { CurrentTablePage, DisplayedQueryResults, AllMatchingTableRows, FullQuery, DatabaseDump }
public enum ExportStatus { Completed, Canceled, Failed }
public sealed record ExportColumn(string Name, int Ordinal, string? ProviderTypeName = null, Type? ClrType = null);
public sealed record ExportProgress(long RowsWritten, TimeSpan Elapsed);
public sealed record ExportOutcome(ExportStatus Status, ExportScope Scope, ExportFormat Format,
    long RowsWritten, TimeSpan Elapsed, string? FinalPath = null, string? Diagnostic = null, string? RetainedTemporaryPath = null)
{
    public string? SourceIdentity { get; init; }
}

/// <summary>Normal enumeration exhaustion certifies completion; a source must throw on incomplete reads.</summary>
public interface IExportSource
{
    IReadOnlyList<ExportColumn> Columns { get; }
    IAsyncEnumerable<IReadOnlyList<object?>> ReadRowsAsync(CancellationToken cancellationToken);
}

public sealed class LoadedExportSource : IExportSource
{
    public IReadOnlyList<ExportColumn> Columns { get; }
    private readonly IReadOnlyList<IReadOnlyList<object?>> _rows;

    public LoadedExportSource(IEnumerable<ExportColumn> columns, IEnumerable<IReadOnlyList<object?>> rows)
    {
        Columns = Array.AsReadOnly(columns.ToArray());
        _rows = rows.Select(row => (IReadOnlyList<object?>)row.Select(CaptureValue).ToArray()).ToArray();
    }

    private static object? CaptureValue(object? value) => value switch
    {
        byte[] bytes => bytes.Clone(),
        JsonNode node => node.DeepClone(),
        JsonElement element => element.Clone(),
        // A cloned element owns its backing data without requiring document disposal.
        JsonDocument document => document.RootElement.Clone(),
        _ => value
    };

    public async IAsyncEnumerable<IReadOnlyList<object?>> ReadRowsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var row in _rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return row;
        }
        await Task.CompletedTask;
    }
}
