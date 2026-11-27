using Avalonia.Controls;
using Avalonia.Input;
using DbConnect.ViewModels;

namespace DbConnect.Views;

public partial class GlobalSearchOverlay : Window
{
    public GlobalSearchOverlay()
    {
        InitializeComponent();
        Opened += (_, _) => this.FindControl<TextBox>("SearchBox")?.Focus();
        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not GlobalSearchViewModel vm) return;

        var list = this.FindControl<ListBox>("ResultsList")!;

        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                vm.CloseCommand.Execute(null);
                break;

            case Key.Enter:
                e.Handled = true;
                vm.NavigateToCommand.Execute(vm.SelectedResult);
                break;

            case Key.Down:
                if (list.ItemCount > 0)
                {
                    var next = (list.SelectedIndex + 1) % list.ItemCount;
                    list.SelectedIndex = next;
                    list.ScrollIntoView(list.SelectedIndex);
                    e.Handled = true;
                }
                break;

            case Key.Up:
                if (list.ItemCount > 0)
                {
                    var prev = list.SelectedIndex <= 0 ? list.ItemCount - 1 : list.SelectedIndex - 1;
                    list.SelectedIndex = prev;
                    list.ScrollIntoView(list.SelectedIndex);
                    e.Handled = true;
                }
                break;
        }
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is GlobalSearchViewModel vm)
            vm.CloseAction = () => Close();
    }
}
