using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.Services.Builders;

namespace DbConnect.ViewModels;

public partial class TableDesignerViewModel : ViewModelBase
{
    private readonly DatabaseSession _session = null!;
    public DatabaseSession? Session => _session;
    private readonly SchemaNode? _tableNode;
    private readonly string? _openedName;
    private TableRebuildDefinition? _loadedDefinition;
    private int _loadVersion;

    [ObservableProperty] private string _tableName = "new_table";
    [ObservableProperty] private ObservableCollection<ColumnDefinition> _columns = new();
    [ObservableProperty] private string? _tableComment;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private string _previewSql = "";

    /// <summary>The reviewed change: <see cref="PreviewSql"/> shows its SQL and Save runs exactly that.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlanAllowsSave))]
    private TableChangePlan _plan = TableChangePlan.None;

    /// <summary>False while the edits are invalid, would lose something the table has, await confirmation, or need a reload.</summary>
    public bool PlanAllowsSave => Plan.CanExecute && !Review.NeedsConfirmation && !Review.RequiresReload;

    /// <summary>Destructive changes to confirm, the commit boundary, and the outcome of the last save.</summary>
    public TableChangeReview Review { get; } = new();

    /// <summary>Names of tabs holding an open transaction on this connection; Save waits until there are none.</summary>
    public Func<IReadOnlyList<string>>? OpenTransactions { get; set; }
    public Func<Task>? OnRequestRefresh { get; set; }
    /// <summary>The loaded design of an existing table, its load state, and whether Save may run.</summary>
    public TableDesignState Design { get; }

    /// <summary>The primary and unique keys and their ordered members.</summary>
    public TableKeysEditor Keys { get; }

    /// <summary>The indexes, their ordered key parts and directions; indexes backing a key are read-only.</summary>
    public TableIndexesEditor Indexes { get; }

    // Foreign keys tab
    /// <summary>The foreign keys, their ordered column pairs and the tables they can reference.</summary>
    public TableForeignKeysEditor ForeignKeys { get; }
    public bool ShowForeignKeyPragmaWarning => _session?.Capabilities.RequiresForeignKeyPragma == true;
    public bool IsEditingExistingTable => _tableNode != null;

    /// <summary>The check constraints.</summary>
    public TableChecksEditor Checks { get; }

    // Validation
    public ObservableCollection<string> ValidationErrors { get; } = new();

    public List<string> AvailableTypes { get; }

    /// <summary>The provider's identity and generated-column rules, which decide the choices and read-only cells below.</summary>
    private readonly ColumnGenerationRules _generationRules = ColumnGenerationRules.None("");
    /// <summary>Identity choices for the Identity cell; the empty choice is none.</summary>
    public IReadOnlyList<string> IdentityKindOptions => ["", .. _generationRules.IdentityKinds.Select(k => ColumnGenerationLabels.Of(k))];
    public IReadOnlyList<string> GeneratedStorageOptions => ["", .. _generationRules.Storages.Select(s => s.ToString())];
    public bool IdentityEditable => _generationRules.IdentityKinds.Count > 0;
    public bool SeedAndIncrementEditable => _generationRules.SeedAndIncrement;
    public bool GenerationEditable => _generationRules.Storages.Count > 0;
    /// <summary>What the engine offers for identity and generated columns, shown above the column grid.</summary>
    public string GenerationSummary => _generationRules.Summary;
    public bool SupportsTableDesignerDropColumn => _session?.Capabilities.SupportsTableDesignerDropColumn == true;
    public string DropColumnUnavailableReason => TableChangePlanner.DropColumnUnavailable;
    public bool SupportsTableDesignerRenameColumn => _session?.Capabilities.SupportsTableDesignerRenameColumn == true;
    public bool SupportsComments => _session?.Capabilities.SupportsObjectComments == true;
    public string RenameColumnUnavailableReason =>
        _session?.Capabilities.SupportsRenameColumn == true
            ? "This provider supports column rename, but DbConnect's current table designer workflow does not implement it yet."
            : "This provider does not support column rename in the current workflow.";

    public TableDesignerViewModel(string tableName, DatabaseSession session, SchemaNode? tableNode = null,
        DatabaseObjectIdentity? identity = null)
    {
        _session = session;
        _tableNode = tableNode;
        Design = new TableDesignState(session, tableNode == null ? null
            : identity ?? tableNode.Identity ?? new DatabaseObjectIdentity(null, null, tableName, SchemaNodeType.Table))
        { Reload = LoadAsync };
        // The keys exist before anything plans: setting the table name below already refreshes the preview.
        var builder = DdlBuilderFactory.For(session.ProviderName);
        Keys = new TableKeysEditor(() => Columns, builder.KeyRules);
        Keys.Changed += UpdatePreview;
        ForeignKeys = new TableForeignKeysEditor(() => Columns, builder.ForeignKeyRules,
            ct => ReferenceTargetCatalog.ListTablesAsync(session, ct),
            session.Provider is ITableDesignSource source ? (table, ct) => source.GetTableDesignAsync(session.ConnectionString, table, ct) : null,
            () => Design.Baseline?.Table ?? (IsEditingExistingTable ? Design.Table : new DatabaseObjectIdentity(null, null, TableName, SchemaNodeType.Table)));
        ForeignKeys.Changed += UpdatePreview;
        Checks = new TableChecksEditor(builder.CheckRules);
        Checks.Changed += UpdatePreview;
        Indexes = new TableIndexesEditor(() => Columns, builder.IndexRules);
        Indexes.Changed += UpdatePreview;
        TableName = tableName;
        _openedName = tableNode == null ? null : tableName;
        Title = tableName;
        AvailableTypes = _session.GetSupportedDataTypes();
        _generationRules = builder.GenerationRules;

        Columns.CollectionChanged += OnColumnsCollectionChanged;
        Review.PropertyChanged += (_, _) => OnPropertyChanged(nameof(PlanAllowsSave));
        UpdatePreview();

        if (_tableNode != null)
        {
            _ = LoadAsync();
        }
        else _ = ForeignKeys.LoadTargetsAsync();
    }

    public TableDesignerViewModel()
    {
        AvailableTypes = new List<string>();
        Design = new TableDesignState(null, null);
        Keys = new TableKeysEditor(() => Columns, TableKeyRules.None(""));
        ForeignKeys = new TableForeignKeysEditor(() => Columns, TableForeignKeyRules.None(""), null, null, () => null);
        Checks = new TableChecksEditor(TableCheckRules.None(""));
        Indexes = new TableIndexesEditor(() => Columns, TableIndexRules.None(""));
    }

    public TableDesignerViewModel(string tableName, IDatabaseProvider provider, string connectionString, SchemaNode? tableNode = null)
        : this(tableName, new DatabaseSession(
            new ConnectionInfo
            {
                Name = tableName,
                Provider = provider.ProviderName,
                ConnectionString = connectionString
            },
            provider), tableNode)
    {
    }

    /// <summary>Derives the change plan from the loaded design and the current edits, and shows it.</summary>
    public void UpdatePreview()
    {
        if (_session == null) return;
        Plan = TableChangePlanner.Plan(Edits());
        Review.Show(Plan, _session.Capabilities.TransactionalDdl);
        PreviewSql = Plan.Sql;
        ValidationErrors.Clear();
        foreach (var problem in Plan.Problems) ValidationErrors.Add(problem);
    }

    private TableDesignerEdits Edits() => new(_session.ProviderName, _session.Capabilities, TableName,
        Columns.ToList(), Indexes.Items.ToList(), ForeignKeys.Items.ToList(), Checks.Items.ToList(), TableComment)
    {
        IsExisting = _tableNode != null,
        Baseline = Design.Baseline,
        OpenedName = _openedName,
        Keys = Keys.Items.ToList(),
        ForeignKeyTarget = ForeignKeys.TargetFor,
        RebuildsColumns = _session.Provider is ITableDefinitionProvider,
        RebuildDefinition = _loadedDefinition
    };

    partial void OnTableNameChanged(string value) => UpdatePreview();
    partial void OnTableCommentChanged(string? value) => UpdatePreview();

    private void OnColumnsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var column in e.OldItems?.OfType<ColumnDefinition>() ?? [])
        {
            column.PropertyChanged -= OnColumnChanged;
            column.PropertyChanging -= OnColumnChanging;
            if (column.IsNew && !Columns.Contains(column))
            {
                Keys.OnColumnRemoved(column);
                Indexes.OnColumnRemoved(column);
            }
        }
        foreach (var column in e.NewItems?.OfType<ColumnDefinition>() ?? [])
        {
            column.PropertyChanged += OnColumnChanged;
            column.PropertyChanging += OnColumnChanging;
            if (column.IsPrimaryKey) Keys.OnPrimaryKeyFlag(column);
        }
        Keys.RefreshColumnNames();
        ForeignKeys.RefreshColumnNames();
        Indexes.RefreshColumnNames();
        UpdatePreview();
    }

    private void OnColumnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is ColumnDefinition column && e.PropertyName == nameof(ColumnDefinition.IsPrimaryKey)) Keys.OnPrimaryKeyFlag(column);
        if (sender is ColumnDefinition renamed && e.PropertyName == nameof(ColumnDefinition.Name))
        {
            Keys.OnColumnRenamed(renamed);
            ForeignKeys.OnColumnRenamed(renamed);
            Indexes.OnColumnRenamed(renamed);
        }
        if (e.PropertyName is nameof(ColumnDefinition.Name) or nameof(ColumnDefinition.IsDropped))
        {
            Keys.RefreshColumnNames();
            ForeignKeys.RefreshColumnNames();
            Indexes.RefreshColumnNames();
        }
        UpdatePreview();
    }

    private void OnColumnChanging(object? sender, PropertyChangingEventArgs e)
    {
        if (sender is ColumnDefinition column && e.PropertyName == nameof(ColumnDefinition.Name))
        {
            Keys.OnColumnRenaming(column);
            ForeignKeys.OnColumnRenaming(column);
            Indexes.OnColumnRenaming(column);
        }
    }

    public async Task LoadAsync() => await LoadCoreAsync();

    /// <summary>Reloads the table; false when its columns or its design could not be read.</summary>
    private async Task<bool> LoadCoreAsync()
    {
        IsLoading = true;
        _loadedDefinition = null;
        Keys.Clear();
        ForeignKeys.Clear();
        Checks.Clear();
        Indexes.Clear();
        var version = ++_loadVersion;
        var design = Design.LoadAsync();
        var loaded = false;
        // Bulk loading plans once at the end instead of once per row.
        Columns.CollectionChanged -= OnColumnsCollectionChanged;
        try
        {
            _loadedDefinition = _session.Provider is ITableDefinitionProvider definitionProvider
                ? await definitionProvider.GetTableDefinitionAsync(_session.ConnectionString, TableName)
                : null;
            var cols = Design.Table is { } table && _session.Provider is IStructuredSchemaProvider structured
                ? await structured.GetColumnsAsync(_session.ConnectionString, table)
                : await _session.GetColumnDefinitionsAsync(TableName);
            if (version != _loadVersion) return false; // a newer load owns the collections

            foreach (var col in Columns)
            {
                col.PropertyChanged -= OnColumnChanged;
                col.PropertyChanging -= OnColumnChanging;
            }
            Columns.Clear();
            foreach (var col in cols)
            {
                col.Original = col.Clone();
                Columns.Add(col);
            }

            // The engine's own CREATE INDEX text, which a SQLite rebuild replays for unchanged indexes.
            var storedSql = (await _session.GetIndexesAsync(TableName)).GroupBy(i => i.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().CreateSql, StringComparer.Ordinal);

            await design;
            if (version != _loadVersion) return false;
            Design.Annotate(Columns);
            if (Design.Baseline is { } baseline)
            {
                Keys.Load(baseline);
                ForeignKeys.Load(baseline);
                Checks.Load(baseline);
                Indexes.Load(baseline, storedSql);
            }
            TableComment = Design.Baseline?.Comment;
            loaded = Design.IsLoaded;
            if (loaded) _ = ForeignKeys.LoadTargetsAsync();
            if (loaded) Review.ReloadReason = null;
        }
        catch (Exception ex)
        {
            _loadedDefinition = null;
            StatusMessage = $"Error loading table: {DiagnosticRedactor.Redact(ex.Message)}";
        }
        finally
        {
            await design; // never throws; a failed column or index load still settles the design state
            // Overlapping loads each unsubscribe; removing first keeps exactly one subscription.
            Columns.CollectionChanged -= OnColumnsCollectionChanged;
            Columns.CollectionChanged += OnColumnsCollectionChanged;
            if (version == _loadVersion)
            {
                foreach (var col in Columns)
                {
                    col.PropertyChanged -= OnColumnChanged;
                    col.PropertyChanged += OnColumnChanged;
                    col.PropertyChanging -= OnColumnChanging;
                    col.PropertyChanging += OnColumnChanging;
                }
                IsLoading = false;
                UpdatePreview();
            }
        }
        return loaded;
    }

    [RelayCommand]
    private void AddColumn()
    {
        Columns.Add(new ColumnDefinition { Name = $"column_{Columns.Count + 1}", IsNew = true });
    }

    [RelayCommand]
    private void DropColumn(ColumnDefinition col)
    {
        if (!SupportsTableDesignerDropColumn)
        {
            StatusMessage = $"⚠️ {DropColumnUnavailableReason}";
            return;
        }

        if (col.IsNew)
        {
            Columns.Remove(col);
        }
        else
        {
            col.IsDropped = !col.IsDropped;
            // Force collection refresh for UI visibility
            var idx = Columns.IndexOf(col);
            Columns.RemoveAt(idx);
            Columns.Insert(idx, col);
        }
    }

    public List<string> ValidateDesigner() => TableDesignerValidation.Validate(Edits());

    [RelayCommand]
    private Task ReloadAsync() => LoadAsync();

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsSaving) return; // one save at a time
        IsSaving = true;
        try
        {
            if (await SaveRefusalAsync() is { } refusal)
            {
                StatusMessage = refusal;
                return;
            }

            StatusMessage = "Saving changes...";
            var outcome = await TableChangeExecutor.ExecuteAsync(_session, Plan.Steps, SchemaChangeScopes.ObjectDetails(_session, _tableNode));
            Review.ShowOutcome(outcome);
            if (OnRequestRefresh != null) await OnRequestRefresh();
            if (outcome.Kind != TableChangeOutcomeKind.Applied)
            {
                StatusMessage = outcome.Summary;
                await RequireReloadAfterAsync(outcome);
                return;
            }
            var reloaded = await LoadCoreAsync();
            StatusMessage = reloaded || _tableNode == null
                ? outcome.Summary
                : "⚠️ The changes were saved, but the table could not be reloaded. Reopen it before making further changes.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Save failed: {DiagnosticRedactor.Redact(ex.Message)}";
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>Why Save may not run the plan now, or null when it may. Re-reads the table last.</summary>
    private async Task<string?> SaveRefusalAsync()
    {
        if (IsLoading) return "Wait for the table definition to finish loading.";
        if (Design.SaveBlockedReason is { } blocked) return $"⚠️ {blocked}";
        if (Review.ReloadReason is { } reload) return $"⚠️ {reload}";
        // Save runs the plan the preview shows; if the edits moved on without a refresh, show the new plan first.
        var reviewed = Plan;
        UpdatePreview();
        if (Plan.Sql != reviewed.Sql) return "⚠️ The planned SQL changed. Review the preview and save again.";
        if (Plan.Problems.FirstOrDefault() is { } problem) return $"⚠️ {problem}";
        if (!Plan.HasChanges) return "No changes to save.";
        if (Review.NeedsConfirmation) return "⚠️ Review the destructive changes and confirm them before saving.";
        if (OpenTransactions?.Invoke() is { Count: > 0 } open)
            return $"⚠️ {string.Join(", ", open)} has an open transaction on this connection. Commit or roll it back first: " +
                   "the schema change would wait for its locks or fail, and the designer never ends another tab's transaction.";
        if (Design.Baseline is not { } baseline) return null;
        StatusMessage = "Checking that the table has not changed...";
        if (await TableChangeExecutor.CheckBaselineAsync(_session, baseline) is not { } drift) return null;
        Review.ReloadReason = drift;
        return $"⚠️ {drift}";
    }

    /// <summary>
    /// After a failed save the edits stay. If nothing was applied and the table still matches the
    /// loaded design, the user may fix the edits and save again; otherwise Save waits for a reload.
    /// </summary>
    private async Task RequireReloadAfterAsync(TableChangeOutcome outcome)
    {
        var reopen = _tableNode == null ? "Close this designer and open the table from the explorer" : "Reload the table";
        if (outcome.Kind != TableChangeOutcomeKind.NotApplied)
        {
            Review.ReloadReason = $"Part of the last save may have been applied. {reopen} before saving again; the edits stay visible until then.";
            return;
        }
        if (Design.Baseline is { } baseline && await TableChangeExecutor.CheckBaselineAsync(_session, baseline) is { } drift)
            Review.ReloadReason = $"The last save failed. {drift}";
    }
}
