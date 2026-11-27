using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DbConnect.ViewModels;

namespace DbConnect.Views;

public partial class FindInDataView : UserControl
{
    public FindInDataView()
    {
        InitializeComponent();

        var resultsTree = this.FindControl<TreeView>("ResultsTree");
        if (resultsTree != null)
        {
            resultsTree.DoubleTapped += ResultsTree_DoubleTapped;
        }
    }

    private void ResultsTree_DoubleTapped(object? sender, TappedEventArgs e)
    {
        var resultsTree = this.FindControl<TreeView>("ResultsTree");
        if (resultsTree != null && resultsTree.SelectedItem is FindInDataRowResult rowResult)
        {
            if (DataContext is FindInDataViewModel vm)
            {
                vm.NavigateToRowCommand.Execute(rowResult);
            }
        }
    }
}
