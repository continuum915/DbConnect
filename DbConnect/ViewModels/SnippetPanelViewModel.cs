using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class SnippetPanelViewModel : ViewModelBase
{
    private readonly SnippetService _service;
    private List<Snippet> _allSnippets = new();

    public ObservableCollection<Snippet> FilteredSnippets { get; } = new();

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPromoteEnabled))]
    private Snippet? _selectedSnippet;

    [ObservableProperty]
    private string _activeConnectionName = string.Empty;

    [ObservableProperty]
    private string _activeDatabaseName = string.Empty;

    public bool IsPromoteEnabled =>
        SelectedSnippet != null && SelectedSnippet.Scope != SnippetScope.Global;

    public event Action<string>? OnSnippetInsertRequested;

    // Used by MainWindowViewModel to open the editor dialog
    public Func<Snippet?, Task<Snippet?>>? ShowSnippetEditorDialog { get; set; }

    public SnippetPanelViewModel(SnippetService? service = null)
    {
        _service = service ?? new SnippetService();
    }

    public async Task LoadAsync()
    {
        var snippets = await _service.LoadAllAsync();
        _allSnippets = snippets.ToList();
        ApplyFilter();
    }

    // Used in tests to seed snippets without going through the service
    public void SetSnippetsForTest(IEnumerable<Snippet> snippets)
    {
        _allSnippets = snippets.ToList();
        ApplyFilter();
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        FilteredSnippets.Clear();
        var text = FilterText?.Trim() ?? string.Empty;

        foreach (var s in _allSnippets)
        {
            if (string.IsNullOrEmpty(text)
                || s.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                || s.Description.Contains(text, StringComparison.OrdinalIgnoreCase)
                || s.Tags.Any(t => t.Contains(text, StringComparison.OrdinalIgnoreCase)))
            {
                FilteredSnippets.Add(s);
            }
        }
    }

    [RelayCommand]
    private async Task NewSnippet()
    {
        if (ShowSnippetEditorDialog == null) return;
        var result = await ShowSnippetEditorDialog(null);
        if (result == null) return;
        await _service.SaveAsync(result);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task EditSnippet()
    {
        if (SelectedSnippet == null || ShowSnippetEditorDialog == null) return;
        var result = await ShowSnippetEditorDialog(SelectedSnippet);
        if (result == null) return;
        await _service.SaveAsync(result);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task DeleteSnippet()
    {
        if (SelectedSnippet == null) return;
        await _service.DeleteAsync(SelectedSnippet.Id);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task PromoteToGlobal()
    {
        if (SelectedSnippet == null || SelectedSnippet.Scope == SnippetScope.Global) return;
        await _service.PromoteToGlobalAsync(SelectedSnippet.Id);
        await LoadAsync();
    }

    [RelayCommand]
    private void InsertSnippet()
    {
        if (SelectedSnippet == null) return;
        SelectedSnippet.LastUsedAt = DateTimeOffset.UtcNow;
        _ = _service.SaveAsync(SelectedSnippet);
        OnSnippetInsertRequested?.Invoke(SelectedSnippet.Body);
    }
}
