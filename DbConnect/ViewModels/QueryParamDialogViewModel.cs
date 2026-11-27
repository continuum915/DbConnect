using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class QueryParamDialogViewModel : ObservableObject
{
    private readonly QueryParamContextService _contextService;
    private readonly string _sql;
    private readonly string? _providerName;
    private readonly string? _connectionName;
    private readonly string? _databaseName;
    private readonly CancellationToken _cancellationToken;

    [ObservableProperty] private string _instructionText;
    [ObservableProperty] private string _providerHint;
    [ObservableProperty] private bool _saveAsSnippet;
    [ObservableProperty] private bool _saveParamContext;
    [ObservableProperty] private string? _contextName;
    [ObservableProperty] private QueryParamContext? _selectedContext;

    public ObservableCollection<ParameterEntryRow> Parameters { get; } = new();
    public ObservableCollection<QueryParamContext> SavedContexts { get; } = new();

    public QueryParamDialogViewModel(
        string sql,
        List<DetectedParameter> detectedParameters,
        QueryParamContextService contextService,
        string? providerName = null,
        string? connectionName = null,
        string? databaseName = null,
        CancellationToken cancellationToken = default)
    {
        _sql = sql;
        _contextService = contextService;
        _providerName = providerName;
        _connectionName = connectionName;
        _databaseName = databaseName;
        _cancellationToken = cancellationToken;

        InstructionText = $"This query contains {detectedParameters.Count} parameter(s).";
        ProviderHint = string.Join(", ", detectedParameters.Select(p => p.ProviderHint).Distinct());

        foreach (var p in detectedParameters)
        {
            Parameters.Add(new ParameterEntryRow(p));
        }

        _ = LoadSavedContextsAsync();
    }

    private async Task LoadSavedContextsAsync()
    {
        try { await LoadSavedContextsCoreAsync(); }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested) { }
    }

    private async Task LoadSavedContextsCoreAsync()
    {
        var fingerprint = await Task.Run(() => _contextService.GetFingerprint(_sql, _cancellationToken), _cancellationToken);
        _cancellationToken.ThrowIfCancellationRequested();
        var allContexts = await _contextService.LoadAsync(_providerName, _connectionName, _databaseName);
        _cancellationToken.ThrowIfCancellationRequested();

        // Filter by fingerprint to show relevant contexts for THIS query first
        var relevant = allContexts.Where(c => c.SqlFingerprint == fingerprint).ToList();
        var others = allContexts.Where(c => c.SqlFingerprint != fingerprint).ToList();

        SavedContexts.Clear();
        foreach (var c in relevant) SavedContexts.Add(c);
        foreach (var c in others) SavedContexts.Add(c);

        if (relevant.Count > 0)
        {
            SelectedContext = relevant[0];
        }
    }

    partial void OnSelectedContextChanged(QueryParamContext? value)
    {
        if (value == null) return;

        foreach (var p in value.Parameters)
        {
            var row = Parameters.FirstOrDefault(r => r.Placeholder == p.Placeholder);
            if (row != null)
            {
                row.Value = p.Value;
                row.DataType = p.DataType;
            }
        }
    }

    [RelayCommand]
    private void Execute()
    {
        // Handled by view closing with result
    }

    public QueryParamDialogResult GetResult(bool confirmed)
    {
        return new QueryParamDialogResult
        {
            Confirmed = confirmed,
            Parameters = Parameters.Select(p => new QueryParamEntry
            {
                Placeholder = p.Placeholder,
                Value = p.Value,
                DataType = p.DataType
            }).ToList(),
            SaveAsSnippet = SaveAsSnippet,
            SaveParamContext = SaveParamContext,
            ContextName = ContextName
        };
    }
}

public partial class ParameterEntryRow : ObservableObject
{
    public string Placeholder { get; }
    public string Hint { get; }

    [ObservableProperty] private object? _value;
    [ObservableProperty] private string _dataType = "string";

    public List<string> DataTypeOptions { get; } = new() { "string", "number", "boolean", "date" };

    public ParameterEntryRow(DetectedParameter p)
    {
        Placeholder = p.Placeholder;
        Hint = p.Style.ToString();
    }
}
