using Avalonia.Controls;
using Avalonia.Input;
using DbConnect.ViewModels;

namespace DbConnect.Views;

public partial class ObjectsTabView : UserControl
{
    public ObjectsTabView()
    {
        InitializeComponent();
    }

    /// <summary>Double-click opens the object (table data, a routine, a saved query).</summary>
    private void Grid_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is ObjectsTabViewModel vm && vm.SelectedRow is { } row)
            vm.OpenCommand.Execute(row);
    }
}
