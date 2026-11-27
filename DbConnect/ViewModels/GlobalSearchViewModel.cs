using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class GlobalSearchViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _mainVm;
    private readonly SchemaIndexer _indexer = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private GlobalSearchResult? _selectedResult;

    public ObservableCollection<GlobalSearchResult> Results { get; } = new();

    public System.Action? CloseAction { get; set; }

    public GlobalSearchViewModel(MainWindowViewModel mainVm)
    {
        _mainVm = mainVm;
    }

    partial void OnSearchTextChanged(string value) => RunSearch();

    private void RunSearch()
    {
        Results.Clear();
        if (string.IsNullOrWhiteSpace(SearchText)) return;

        var connections = _mainVm.ActiveConnections
            .Select(kvp => (kvp.Value.Name, (System.Collections.Generic.IEnumerable<SchemaNode>)kvp.Key.Children))
            .ToList();

        var found = _indexer.SearchAll(SearchText, connections);
        foreach (var r in found)
            Results.Add(r);

        SelectedResult = Results.FirstOrDefault();
    }

    [RelayCommand]
    private void NavigateTo(GlobalSearchResult? result)
    {
        result ??= SelectedResult;
        if (result?.Node == null) return;

        // Find the actual SchemaNode in the active tree (the node reference in the result)
        _mainVm.SelectedSchemaNode = result.Node;
        CloseAction?.Invoke();
    }

    [RelayCommand]
    private void Close()
    {
        CloseAction?.Invoke();
    }
}
