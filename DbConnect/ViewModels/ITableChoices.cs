using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;

namespace DbConnect.ViewModels;

/// <summary>A list of tables to tick, with Select All and Select None, as Export Data and Dump SQL File show it.</summary>
public interface ITableChoices
{
    ObservableCollection<ExportTableChoice> Tables { get; }
    string TablesCaption { get; }
    IRelayCommand SelectAllCommand { get; }
    IRelayCommand SelectNoneCommand { get; }
}
