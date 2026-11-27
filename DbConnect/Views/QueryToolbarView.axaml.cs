using Avalonia.Controls;
using Avalonia.Interactivity;
using DbConnect.Models;
using DbConnect.ViewModels;

namespace DbConnect.Views;

public partial class QueryToolbarView : UserControl
{
    public QueryToolbarView()
    {
        InitializeComponent();
        // The More menu's lists: picking an entry runs the tab's existing open, load or insert command for it.
        foreach (var name in new[] { "FavoritesMenu", "RecentMenu", "SnippetMenu" })
            this.FindControl<MenuItem>(name)?.AddHandler(MenuItem.ClickEvent, OnListItemClick);
    }

    private void OnListItemClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not QueryTabViewModel tab || e.Source is not MenuItem { DataContext: var item }) return;
        switch (item)
        {
            case SavedQueryDefinition favorite:
                tab.SelectedSavedQuery = favorite;
                tab.OpenSelectedSavedQueryCommand.Execute(null);
                break;
            case QueryHistoryEntry recent:
                tab.SelectedHistoryEntry = recent;
                tab.LoadSelectedHistoryCommand.Execute(null);
                break;
            case SqlSnippetDefinition snippet:
                tab.SelectedSnippet = snippet;
                tab.InsertSelectedSnippetCommand.Execute(null);
                break;
        }
    }
}
