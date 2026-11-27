using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.Services.Builders;

namespace DbConnect.ViewModels;

public partial class IndexManagerViewModel : ViewModelBase
{
    private readonly DatabaseSession _session = null!;
    public DatabaseSession? Session => _session;
    private readonly SchemaNode _tableNode = null!;

    [ObservableProperty] private ObservableCollection<IndexDefinition> _indexes = new();
    [ObservableProperty] private ObservableCollection<IndexColumnEntry> _columnEntries = new();
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private string _previewSql = "";
    public Func<Task>? OnRequestRefresh { get; set; }

    // New index form properties
    [ObservableProperty] private string _newIndexName = "";
    [ObservableProperty] private bool _newIndexIsUnique;
    public bool SupportsIndexManagement => _session?.Capabilities.SupportsIndexes == true;
    public string UnsupportedReason => "This provider does not support index management in the current workflow.";
    public bool SupportsIndexWhereClause => _session?.Capabilities.SupportsIndexWhereClause == true;
    public bool SupportsIncludedColumns => _session?.Capabilities.SupportsIncludedIndexColumns == true;

    [ObservableProperty] private string _newIndexWhereClause = "";
    [ObservableProperty] private string _newIndexIncludedColumns = "";

    public IndexManagerViewModel(SchemaNode tableNode, DatabaseSession session)
    {
        _session = session;
        _tableNode = tableNode;
        Title = $"{tableNode.Name} Indexes";

        _ = LoadAsync();
    }

    public IndexManagerViewModel() { }

    public IndexManagerViewModel(SchemaNode tableNode, IDatabaseProvider provider, string connectionString)
        : this(tableNode, new DatabaseSession(
            new ConnectionInfo
            {
                Name = tableNode.Name,
                Provider = provider.ProviderName,
                ConnectionString = connectionString
            },
            provider))
    {
    }

    public void UpdateIndexPreview()
    {
        if (_session == null || _tableNode == null || !SupportsIndexManagement) return;
        var tableName = _tableNode.RawName ?? _tableNode.Name;
        var selectedEntries = ColumnEntries.Where(e => e.IsSelected).ToList();
        var includedCols = string.IsNullOrWhiteSpace(NewIndexIncludedColumns)
            ? new System.Collections.Generic.List<string>()
            : NewIndexIncludedColumns.Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries).ToList();
        PreviewSql = DdlBuilderFactory.For(_session.ProviderName).GenerateCreateIndex(new IndexDefinition
        {
            Name = NewIndexName,
            TableName = tableName,
            Columns = selectedEntries.Select(e => e.ColumnName).ToList(),
            ColumnDirections = selectedEntries.Select(e => e.Direction).ToList(),
            IsUnique = NewIndexIsUnique,
            WherePredicate = string.IsNullOrWhiteSpace(NewIndexWhereClause) ? null : NewIndexWhereClause,
            IncludedColumns = includedCols
        });
    }

    partial void OnNewIndexNameChanged(string value) => UpdateIndexPreview();
    partial void OnNewIndexIsUniqueChanged(bool value) => UpdateIndexPreview();
    partial void OnNewIndexWhereClauseChanged(string value) => UpdateIndexPreview();
    partial void OnNewIndexIncludedColumnsChanged(string value) => UpdateIndexPreview();

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (!SupportsIndexManagement)
        {
            StatusMessage = $"⚠️ {UnsupportedReason}";
            return;
        }

        IsLoading = true;
        try
        {
            var tableName = _tableNode.RawName ?? _tableNode.Name;
            var idxs = await _session.GetIndexesAsync(tableName);
            Indexes.Clear();
            foreach (var idx in idxs) Indexes.Add(idx);

            var cols = await _session.GetColumnDefinitionsAsync(tableName);
            ColumnEntries.Clear();
            foreach (var col in cols) ColumnEntries.Add(new IndexColumnEntry(col.Name));

            NewIndexName = $"idx_{tableName}_{Indexes.Count + 1}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void NotifyColumnSelectionChanged() => UpdateIndexPreview();

    [RelayCommand]
    private async Task AddIndexAsync()
    {
        if (!SupportsIndexManagement)
        {
            StatusMessage = $"⚠️ {UnsupportedReason}";
            return;
        }

        if (string.IsNullOrWhiteSpace(NewIndexName) || !ColumnEntries.Any(e => e.IsSelected))
        {
            StatusMessage = "⚠️ Please provide a name and select at least one column.";
            return;
        }

        try
        {
            var tableName = _tableNode.RawName ?? _tableNode.Name;
            var selectedEntries = ColumnEntries.Where(e => e.IsSelected).ToList();
            var includedCols = string.IsNullOrWhiteSpace(NewIndexIncludedColumns)
                ? new System.Collections.Generic.List<string>()
                : NewIndexIncludedColumns.Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries).ToList();
            var idx = new IndexDefinition
            {
                Name = NewIndexName,
                TableName = tableName,
                Columns = selectedEntries.Select(e => e.ColumnName).ToList(),
                ColumnDirections = selectedEntries.Select(e => e.Direction).ToList(),
                IsUnique = NewIndexIsUnique,
                WherePredicate = string.IsNullOrWhiteSpace(NewIndexWhereClause) ? null : NewIndexWhereClause,
                IncludedColumns = includedCols
            };

            var ddl = DdlBuilderFactory.For(_session.ProviderName).GenerateCreateIndex(idx);
            await _session.ExecuteNonQueryAsync(ddl, SchemaChangeScopes.ObjectDetails(_session, _tableNode));
            StatusMessage = $"✅ Index {NewIndexName} created.";
            if (OnRequestRefresh != null) await OnRequestRefresh();
            foreach (var e in ColumnEntries) e.IsSelected = false;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Create failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DropIndexAsync(IndexDefinition idx)
    {
        if (!SupportsIndexManagement)
        {
            StatusMessage = $"⚠️ {UnsupportedReason}";
            return;
        }

        try
        {
            var tableName = _tableNode.RawName ?? _tableNode.Name;
            var ddl = DdlBuilderFactory.For(_session.ProviderName).GenerateDropIndex(idx.Name, tableName);
            await _session.ExecuteNonQueryAsync(ddl, SchemaChangeScopes.ObjectDetails(_session, _tableNode));
            StatusMessage = $"✅ Index {idx.Name} dropped.";
            if (OnRequestRefresh != null) await OnRequestRefresh();
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Error: {ex.Message}";
        }
    }
}
