using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.Services.Builders;

namespace DbConnect.ViewModels;

public enum TableDesignLoadState
{
    /// <summary>A new table: there is nothing to load.</summary>
    New,
    Loading,
    Loaded,
    Failed,
    /// <summary>The provider has no design reader.</summary>
    Unavailable
}

/// <summary>
/// The immutable design an existing table was loaded with, and the state of loading it, for
/// the table designer (issue 06). It also decides whether Save may run.
/// </summary>
public sealed partial class TableDesignState : ObservableObject
{
    public const string PreservedLabel = "Preserved — editing not available yet";

    private readonly DatabaseSession? _session;
    private readonly DatabaseObjectIdentity? _table;
    private CancellationTokenSource? _load;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsFailed), nameof(IsUnavailable), nameof(IsLoaded), nameof(SaveBlockedReason), nameof(CanSave))]
    private TableDesignLoadState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveBlockedReason), nameof(CanSave), nameof(HasUnrepresented))]
    private TableDesign? _baseline;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveBlockedReason))]
    private string? _error;

    /// <summary>Why the editable column list does not match the design's columns, or null when it does.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveBlockedReason), nameof(CanSave))]
    private string? _columnsIncomplete;

    public TableDesignState(DatabaseSession? session, DatabaseObjectIdentity? table)
    {
        _session = session;
        _table = table;
        _state = table == null ? TableDesignLoadState.New : TableDesignLoadState.Loading;
    }

    /// <summary>The existing table being designed; null for a new table.</summary>
    public DatabaseObjectIdentity? Table => _table;

    public bool IsLoading => State == TableDesignLoadState.Loading;
    public bool IsFailed => State == TableDesignLoadState.Failed;
    public bool IsUnavailable => State == TableDesignLoadState.Unavailable;
    public bool IsLoaded => State == TableDesignLoadState.Loaded;
    public bool HasUnrepresented => Baseline?.Unrepresented.Count > 0;

    public ObservableCollection<string> Unrepresented { get; } = new();
    public ObservableCollection<TableDesignTrigger> Triggers { get; } = new();
    public ObservableCollection<TableDesignOption> Options { get; } = new();

    /// <summary>Engine limits of this table's provider, e.g. "Triggers: Not supported by DuckDB".</summary>
    public ObservableCollection<string> Limits { get; } = new();

    public string? TriggersUnsupported => Baseline?.UnsupportedReason(TableDesignFeature.Triggers);
    public string? ColumnLimits => LimitsOf(TableDesignFeature.ColumnCollation, TableDesignFeature.Identity, TableDesignFeature.GeneratedColumns,
        TableDesignFeature.StoredGeneratedColumns, TableDesignFeature.VirtualGeneratedColumns, TableDesignFeature.GeneratedExpressionChanges,
        TableDesignFeature.ColumnComments);
    public string? IndexLimits => LimitsOf(TableDesignFeature.IndexIncludedColumns, TableDesignFeature.IndexFilter,
        TableDesignFeature.IndexExpressions, TableDesignFeature.IndexDescending, TableDesignFeature.IndexMethod);

    private string? LimitsOf(params TableDesignFeature[] features) =>
        Baseline?.Unsupported.Where(u => features.Contains(u.Key)).OrderBy(u => u.Key).Select(u => $"{Words(u.Key)}: {u.Value}").ToList()
            is { Count: > 0 } limits ? string.Join(". ", limits) + "." : null;
    public string? OptionsUnsupported => Baseline?.UnsupportedReason(TableDesignFeature.Options);

    /// <summary>Why Save is disabled, or null when it may run.</summary>
    public string? SaveBlockedReason => State switch
    {
        TableDesignLoadState.New => null,
        TableDesignLoadState.Loading => "Save is disabled until the table design has loaded.",
        TableDesignLoadState.Failed => $"Save is disabled because the table design could not be loaded: {Error}",
        TableDesignLoadState.Unavailable => "Save is disabled because this provider cannot read the complete table design, so saving could lose part of it.",
        _ when ColumnsIncomplete is { } columns => $"Save is disabled. {columns}",
        _ => Baseline != null && TableDesignSaveGate.Reasons(Baseline).FirstOrDefault() is { } reason ? $"Save is disabled. {reason}" : null
    };

    public bool CanSave => SaveBlockedReason == null;

    /// <summary>The designer's full reload, so a retry also refreshes and annotates its columns and indexes.</summary>
    public Func<Task>? Reload { get; set; }

    [RelayCommand]
    private Task RetryAsync() => Reload?.Invoke() ?? LoadAsync();

    /// <summary>Loads the design in the background; a newer load supersedes an older one.</summary>
    public async Task LoadAsync()
    {
        if (_table == null || _session == null) return;
        _load?.Cancel();
        var load = _load = new CancellationTokenSource();
        State = TableDesignLoadState.Loading;
        Error = null;
        try
        {
            if (_session.Provider is not ITableDesignSource source) throw new NotSupportedException();
            var connectionString = _session.ConnectionString;
            var table = _table;
            var design = await Task.Run(() => source.GetTableDesignAsync(connectionString, table, load.Token), load.Token);
            if (load.IsCancellationRequested) return;
            Show(design);
            State = TableDesignLoadState.Loaded;
        }
        catch (NotSupportedException)
        {
            if (!load.IsCancellationRequested) State = TableDesignLoadState.Unavailable;
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (load.IsCancellationRequested) return;
            Baseline = null;
            Error = ex is TableDefinitionUnavailableException ? ex.Message : DiagnosticRedactor.Redact(ex.Message);
            State = TableDesignLoadState.Failed;
        }
    }

    private void Show(TableDesign design)
    {
        Baseline = design;
        Replace(Unrepresented, design.Unrepresented.Select(g => g.Description));
        Replace(Triggers, design.Triggers);
        Replace(Options, design.Options);
        Replace(Limits, design.Unsupported.OrderBy(u => u.Key).Select(u => $"{Words(u.Key)}: {u.Value}"));
        OnPropertyChanged(nameof(TriggersUnsupported));
        OnPropertyChanged(nameof(ColumnLimits));
        OnPropertyChanged(nameof(IndexLimits));
        OnPropertyChanged(nameof(OptionsUnsupported));
    }

    /// <summary>
    /// Makes the loaded design the editing baseline: each column gets its loaded design, comment,
    /// declared type (split into type name, length, precision and scale by the engine's rules),
    /// collation, identity and generation, also on its original so an unchanged column plans nothing.
    /// </summary>
    public void Annotate(Collection<ColumnDefinition> columns)
    {
        ColumnsIncomplete = null;
        if (Baseline is not { } design) return;
        ColumnsIncomplete = Incomplete(columns.Where(c => !c.IsNew).Select(c => c.Name).ToList(), design);
        var builder = DdlBuilderFactory.For(design.ProviderName);
        var dialect = _session?.Capabilities.BatchDialect ?? SqlBatchDialect.Standard;
        foreach (var column in columns.Where(c => !c.IsNew))
        {
            column.Loaded = design.Columns.FirstOrDefault(c => c.Name.Equals(column.Name, StringComparison.Ordinal))
                            ?? design.Columns.FirstOrDefault(c => c.Name.Equals(column.Name, StringComparison.OrdinalIgnoreCase));
            column.Comment = column.Loaded?.Comment;
            if (column.Loaded != null) Apply(column, column.Loaded, builder, dialect);
            if (column.Original == null) continue;
            column.Original.Loaded = column.Loaded;
            column.Original.Comment = column.Loaded?.Comment;
            if (column.Loaded != null) Apply(column.Original, column.Loaded, builder, dialect);
        }
    }

    /// <summary>
    /// The column list is read separately from the design, through metadata that can hide columns a login has no
    /// privilege on (PostgreSQL's information_schema). A list that is not exactly the design's columns is never
    /// treated as the table's columns.
    /// </summary>
    private static string? Incomplete(IReadOnlyList<string> listed, TableDesign design)
    {
        var missing = design.Columns.Select(c => c.Name).Where(n => !listed.Contains(n, StringComparer.Ordinal)).ToList();
        var extra = listed.Where(n => !design.Columns.Any(c => c.Name.Equals(n, StringComparison.Ordinal))).ToList();
        if (missing.Count == 0 && extra.Count == 0) return null;
        var parts = new List<string>();
        if (missing.Count > 0) parts.Add($"missing {string.Join(", ", missing)}");
        if (extra.Count > 0) parts.Add($"not in the design: {string.Join(", ", extra)}");
        return $"The table's columns could not all be read ({string.Join("; ", parts)}). The login may lack privileges on " +
               "the table; reload once the columns can be read.";
    }

    private static void Apply(ColumnDefinition column, TableDesignColumn loaded, IDdlBuilder builder, SqlBatchDialect dialect)
    {
        ColumnTypeText.Apply(column, loaded, builder.TypeRules, builder.Quote, dialect);
        column.Generation = ColumnGenerationSettings.From(loaded);
    }

    private static string Words(TableDesignFeature feature) => feature switch
    {
        TableDesignFeature.ColumnCollation => "Column collations",
        TableDesignFeature.Identity => "Identity columns",
        TableDesignFeature.GeneratedColumns => "Generated columns",
        TableDesignFeature.StoredGeneratedColumns => "Stored generated columns",
        TableDesignFeature.VirtualGeneratedColumns => "Virtual generated columns",
        TableDesignFeature.GeneratedExpressionChanges => "Changing a generated column's expression",
        TableDesignFeature.ColumnComments => "Column comments",
        TableDesignFeature.TableComment => "Table comments",
        TableDesignFeature.ForeignKeyActions => "Foreign key actions",
        TableDesignFeature.IndexIncludedColumns => "Included index columns",
        TableDesignFeature.IndexFilter => "Filtered indexes",
        TableDesignFeature.IndexExpressions => "Index expressions",
        TableDesignFeature.IndexDescending => "Descending index keys",
        TableDesignFeature.IndexMethod => "Index methods",
        TableDesignFeature.ConstraintChanges => "Changing keys, foreign keys and checks after creation",
        _ => feature.ToString()
    };

    private static void Replace<T>(Collection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }
}
