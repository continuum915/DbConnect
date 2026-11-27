using Avalonia.Controls;
using Avalonia.Interactivity;
using DbConnect.Models;
using DbConnect.ViewModels;

namespace DbConnect.Views;

public partial class SnippetEditorDialog : Window
{
    public SnippetEditorDialog()
    {
        InitializeComponent();
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SnippetEditorDialogViewModel vm)
        {
            vm.SaveCommand.Execute(null);
            Close(vm.Result);
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}
