using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DbConnect.ViewModels;

namespace DbConnect.Views;

public partial class CommandPaletteView : Window
{
    public CommandPaletteView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown_Handler, RoutingStrategies.Tunnel);
    }

    private void OnKeyDown_Handler(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (DataContext is CommandPaletteViewModel vm)
            {
                vm.ExecuteSelectedCommand.Execute(null);
                Close();
            }
            e.Handled = true;
        }
    }
}
