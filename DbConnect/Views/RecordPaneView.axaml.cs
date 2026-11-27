using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace DbConnect.Views;

public partial class RecordPaneView : UserControl
{
    public RecordPaneView()
    {
        InitializeComponent();
    }

    // View Value… and Open referenced row are the grid's own cell commands, which act on the focused cell this pane
    // shows; running the grid's menu items keeps one implementation of each.
    private void ViewValue_Click(object? sender, RoutedEventArgs e) => RunGridMenuItem("ViewValueMenuItem");

    private void OpenReferenced_Click(object? sender, RoutedEventArgs e) => RunGridMenuItem("ReferencedRowMenuItem");

    private void RunGridMenuItem(string name)
    {
        if (this.FindAncestorOfType<TableDataTabView>()?.FindControl<MenuItem>(name) is { IsEnabled: true } item)
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }
}
