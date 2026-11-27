using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class AddTableDialogViewModel : ViewModelBase
{
    public override string Title => "Add Table";

    [ObservableProperty] private ObservableCollection<string> _availableTables = new();
    [ObservableProperty] private string? _selectedTable;
    [ObservableProperty] private string _alias = string.Empty;

    public AddTableDialogViewModel(DatabaseSession? session)
    {
        if (session != null)
            _ = LoadTablesAsync(session);
    }

    private async Task LoadTablesAsync(DatabaseSession session)
    {
        try
        {
            var schema = await session.GetSchemaAsync();
            AvailableTables.Clear();
            foreach (var root in schema)
                foreach (var child in root.Children)
                {
                    if (child.Type == DbConnect.Models.SchemaNodeType.Table)
                        AvailableTables.Add(child.RawName ?? child.Name);
                    else
                        foreach (var grandchild in child.Children)
                            if (grandchild.Type == DbConnect.Models.SchemaNodeType.Table)
                                AvailableTables.Add(grandchild.RawName ?? grandchild.Name);
                }
        }
        catch { /* schema load failures are non-fatal */ }
    }
}
