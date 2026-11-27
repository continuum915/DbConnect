using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;

namespace DbConnect.ViewModels;

public partial class ForeignKeyLookupViewModel : ViewModelBase
{
    private readonly TableDataTabViewModel _table;
    private readonly ForeignKeyRelationship _relationship;
    private int _pageIndex;
    private bool _hasNextPage;

    [ObservableProperty]
    private IReadOnlyList<ForeignKeyLookupValue> _values = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChooseCommand))]
    private ForeignKeyLookupValue? _selectedValue;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyCanExecuteChangedFor(nameof(ChooseCommand))]
    private bool _isLoading;

    [ObservableProperty]
    private string? _message;

    public string KeyColumns => string.Join(" · ", _relationship.ReferencedColumns);
    public event Action<ForeignKeyLookupValue>? Chosen;

    public ForeignKeyLookupViewModel(TableDataTabViewModel table, ForeignKeyRelationship relationship)
    {
        _table = table;
        _relationship = relationship;
        Title = $"Look up {relationship.ReferencedTable}";
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        SelectedValue = null;
        Values = [];
        _hasNextPage = false;
        try
        {
            Values = await _table.LoadForeignKeyValuesAsync(_relationship, _pageIndex);
            _hasNextPage = Values.Count == 100;
            Message = $"Page {_pageIndex + 1}";
        }
        catch (Exception ex) { Message = ex.Message; }
        finally { IsLoading = false; }
    }

    private bool CanPrevious() => !IsLoading && _pageIndex > 0;
    private bool CanNext() => !IsLoading && _hasNextPage;
    private bool CanChoose() => !IsLoading && SelectedValue != null;

    [RelayCommand(CanExecute = nameof(CanPrevious))]
    private async Task PreviousAsync() { _pageIndex--; await LoadAsync(); }

    [RelayCommand(CanExecute = nameof(CanNext))]
    private async Task NextAsync() { _pageIndex++; await LoadAsync(); }

    [RelayCommand(CanExecute = nameof(CanChoose))]
    private void Choose()
    {
        if (SelectedValue != null) Chosen?.Invoke(SelectedValue);
    }
}
