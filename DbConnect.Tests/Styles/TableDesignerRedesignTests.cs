using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DbConnect.Models;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.Tests.Styles;

/// <summary>UI redesign 08: the table designer's sub-tabs, field properties panel and toolbar.</summary>
public sealed class TableDesignerRedesignTests
{
    [AvaloniaFact]
    public void SubTabs_FollowTheDesign_AndTheFieldPropertiesFollowTheSelectedField()
    {
        var designer = new TableDesignerViewModel();
        var view = new TableDesignerView { DataContext = designer };
        using var host = ThemedHost.Show(view, "Light", height: 800, width: 1300);

        var tabs = view.GetVisualDescendants().OfType<TabControl>().First();
        Assert.Equal(["Fields", "Indexes", "Foreign Keys", "Constraints", "Triggers", "Options", "Comment", "SQL Preview"],
            tabs.Items.OfType<TabItem>().Select(t => t.Header?.ToString()));

        designer.AddColumnCommand.Execute(null);
        designer.AddColumnCommand.Execute(null);
        designer.Columns[0].Name = "id";
        designer.Columns[1].Name = "title";
        host.Window.UpdateLayout();
        var panel = Named<Border>(view, "FieldProperties");
        Assert.False(panel.IsVisible);

        var grid = Named<DataGrid>(view, "ColumnGrid");
        grid.SelectedItem = designer.Columns[1];
        host.Window.UpdateLayout();
        Assert.True(panel.IsVisible);
        Assert.Contains("title", Named<TextBlock>(view, "FieldPropertiesTitle").Inlines!.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text));

        grid.SelectedItem = designer.Columns[0];
        host.Window.UpdateLayout();
        Assert.Contains("id", Named<TextBlock>(view, "FieldPropertiesTitle").Inlines!.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text));
        Assert.Equal("id", designer.Columns[0].Name); // the panel's title must not write back into the field
    }

    [AvaloniaFact]
    public void DeleteField_RunsTheDropForTheSelectedField()
    {
        var designer = new TableDesignerViewModel();
        var view = new TableDesignerView { DataContext = designer };
        using var host = ThemedHost.Show(view, "Light", height: 800, width: 1300);
        designer.AddColumnCommand.Execute(null);
        designer.AddColumnCommand.Execute(null);
        var second = designer.Columns[1];
        host.Window.UpdateLayout();

        var delete = Named<Button>(view, "DeleteFieldButton");
        Assert.False(delete.IsEnabled);
        Named<DataGrid>(view, "ColumnGrid").SelectedItem = second;
        host.Window.UpdateLayout();
        Assert.True(delete.IsEnabled);
        delete.Command!.Execute(delete.CommandParameter);

        // The button runs the grid's own drop for the selected field: removed where the engine can drop columns,
        // otherwise refused with the reason.
        if (designer.SupportsTableDesignerDropColumn)
            Assert.DoesNotContain(second, designer.Columns);
        else
            Assert.Contains(designer.DropColumnUnavailableReason, designer.StatusMessage);
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Concat(root.GetLogicalDescendantsOfType<T>()).First(c => c.Name == name);
}

internal static class LogicalTreeTestExtensions
{
    public static IEnumerable<T> GetLogicalDescendantsOfType<T>(this Control root) where T : Control =>
        Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(root).OfType<T>();
}
