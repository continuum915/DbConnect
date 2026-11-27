using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DbConnect.ViewModels;

namespace DbConnect.Views;

public partial class SnippetPanelView : UserControl
{
    public SnippetPanelView()
    {
        InitializeComponent();

        var list = this.FindControl<ListBox>("SnippetList")!;
        list.DoubleTapped += OnSnippetDoubleTapped;
    }

    private void OnSnippetDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is SnippetPanelViewModel vm && vm.InsertSnippetCommand.CanExecute(null))
            vm.InsertSnippetCommand.Execute(null);
    }
}
