using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DbConnect.ViewModels;

public partial class CommandPaletteViewModel : ViewModelBase
{
    public record PaletteAction(string Name, string Category, ICommand Command, object? Parameter = null);

    private readonly List<PaletteAction> _allActions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredActions))]
    private string _searchText = string.Empty;

    public IEnumerable<PaletteAction> FilteredActions => string.IsNullOrWhiteSpace(SearchText)
        ? _allActions
        : _allActions.Where(a =>
            a.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || a.Category.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

    [ObservableProperty]
    private PaletteAction? _selectedAction;

    public CommandPaletteViewModel(IEnumerable<PaletteAction> actions)
    {
        _allActions = actions.ToList();
        Title = "Command Palette";
    }

    public CommandPaletteViewModel() : this(Enumerable.Empty<PaletteAction>()) { }

    [RelayCommand]
    private void ExecuteSelected()
    {
        if (SelectedAction is { } action && action.Command.CanExecute(action.Parameter))
            action.Command.Execute(action.Parameter);
    }
}
