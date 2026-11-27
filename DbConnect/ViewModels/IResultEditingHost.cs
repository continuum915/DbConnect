using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>Result state and database lifetime supplied by a table or editable query tab.</summary>
public interface IResultEditingHost
{
    ObservableCollection<EditableRow> Rows { get; }
    ObservableCollection<string> Columns { get; }
    IReadOnlyList<string> PrimaryKeyColumns { get; }
    EditableRow? SelectedRow { get; set; }
    bool IsCommitting { get; set; }
    bool CanEditExistingRows { get; }
    bool HasConnection { get; }
    string CommitModeSummary { get; }
    Task<IDbSession> OpenEditingSessionAsync();
    Task ReloadResultsAsync();
    void SetEditingStatus(string? message);
    void RefreshEditingState();
}
