using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;

namespace DbConnect.ViewModels;

/// <summary>A Dump SQL File mode as the dialog lists it, by its menu name.</summary>
public sealed record DumpModeChoice(DumpMode Mode)
{
    public override string ToString() => Mode.Title();
}

/// <summary>
/// Dump SQL File on several selected tables: the explorer's selection arrives ticked in the Export Data table list,
/// beside the other tables of the same parent. The user ticks the tables to dump and picks the mode; the dump then
/// writes only the ticked tables, in dependency order.
/// </summary>
public sealed partial class DumpTablesViewModel : ViewModelBase, ITableChoices
{
    public DumpTablesViewModel(IReadOnlyList<ExportTableChoice> tables, DumpMode mode)
    {
        Tables = new(tables);
        foreach (var table in Tables) table.PropertyChanged += OnTableChanged;
        _mode = Modes.Single(m => m.Mode == mode);
    }

    public ObservableCollection<ExportTableChoice> Tables { get; }
    public string TablesCaption => "Tables to dump (referenced tables are written first):";
    public IReadOnlyList<DumpModeChoice> Modes { get; } = Enum.GetValues<DumpMode>().Select(m => new DumpModeChoice(m)).ToList();
    [ObservableProperty] private DumpModeChoice _mode;

    /// <summary>True once the user chose Dump; the dialog closes then.</summary>
    public bool Confirmed { get; private set; }
    public event Action? CloseRequested;

    public IReadOnlyList<DatabaseObjectIdentity> SelectedTables => Tables.Where(t => t.IsSelected).Select(t => t.Identity).ToList();

    private void OnTableChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExportTableChoice.IsSelected)) DumpCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand] private void SelectAll() { foreach (var t in Tables) t.IsSelected = true; }
    [RelayCommand] private void SelectNone() { foreach (var t in Tables) t.IsSelected = false; }

    private bool CanDump() => Tables.Any(t => t.IsSelected);

    [RelayCommand(CanExecute = nameof(CanDump))]
    private void Dump()
    {
        Confirmed = true;
        CloseRequested?.Invoke();
    }
}
