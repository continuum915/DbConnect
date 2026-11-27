using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>
/// The Favorites and Window menus and the window title: saved favourites that open in a new query tab, next and
/// previous tab, and "&lt;active tab&gt; — DbConnect".
/// </summary>
public sealed partial class WindowMenuState : ObservableObject
{
    public const string AppName = "DbConnect";

    private readonly ObservableCollection<ViewModelBase> _tabs;
    private readonly Func<ViewModelBase?> _selected;
    private readonly Action<ViewModelBase> _select;
    private readonly Action<string> _openScript;
    private readonly QueryWorkspaceService _workspace;

    [ObservableProperty]
    private string _windowTitle = AppName;

    public WindowMenuState(
        ObservableCollection<ViewModelBase> tabs,
        Func<ViewModelBase?> selected,
        Action<ViewModelBase> select,
        Action<string> openScript,
        QueryWorkspaceService workspace)
    {
        _tabs = tabs;
        _selected = selected;
        _select = select;
        _openScript = openScript;
        _workspace = workspace;
    }

    /// <summary>The open tabs, listed in the Window menu.</summary>
    public ObservableCollection<ViewModelBase> Tabs => _tabs;

    /// <summary>Saved favourites (saved queries), listed in the Favorites menu.</summary>
    public ObservableCollection<SavedQueryDefinition> Favorites { get; } = [];

    public bool HasNoFavorites => Favorites.Count == 0;

    public void RefreshTitle()
    {
        var tab = _selected();
        WindowTitle = tab == null ? AppName : $"{tab.Title} — {AppName}";
    }

    /// <summary>Reloads the favourites; called when the Favorites menu opens.</summary>
    [RelayCommand]
    private async Task RefreshFavorites()
    {
        var saved = await _workspace.LoadSavedQueriesAsync();
        Favorites.Clear();
        foreach (var query in saved)
            Favorites.Add(query);
        OnPropertyChanged(nameof(HasNoFavorites));
    }

    [RelayCommand]
    private void OpenFavorite(SavedQueryDefinition? favorite)
    {
        if (favorite != null)
            _openScript(favorite.Sql);
    }

    [RelayCommand]
    private void SelectTab(ViewModelBase? tab)
    {
        if (tab != null && _tabs.Contains(tab))
            _select(tab);
    }

    [RelayCommand]
    private void NextTab() => Step(+1);

    [RelayCommand]
    private void PreviousTab() => Step(-1);

    private void Step(int direction)
    {
        if (_tabs.Count == 0) return;
        var index = _selected() is { } current ? _tabs.IndexOf(current) : -1;
        var next = ((index + direction) % _tabs.Count + _tabs.Count) % _tabs.Count;
        _select(_tabs.ElementAt(next));
    }
}
