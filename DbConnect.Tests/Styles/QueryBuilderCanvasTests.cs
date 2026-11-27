using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using DbConnect.Models;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.Tests.Styles;

/// <summary>The Query Builder canvas: cards and join lines built in code-behind follow the theme; an empty canvas says what to do.</summary>
public sealed class QueryBuilderCanvasTests
{
    [AvaloniaTheory]
    [MemberData(nameof(ThemedHost.Variants), MemberType = typeof(ThemedHost))]
    public void TableCardsAndJoins_TakeTheirColoursFromTheTheme(string variant)
    {
        var viewModel = new QueryBuilderViewModel();
        var view = new QueryBuilderView { DataContext = viewModel };
        using var host = ThemedHost.Show(view, variant, height: 800, width: 1200);

        AddTable(viewModel, "customers");
        AddTable(viewModel, "orders");
        viewModel.AddJoin(new JoinDefinition { LeftTable = "customers", LeftColumn = "id", RightTable = "orders", RightColumn = "customer_id", JoinType = "LEFT" });
        host.Window.UpdateLayout();

        var canvas = view.FindControl<Canvas>("QueryCanvas")!;
        var card = canvas.Children.OfType<Border>().First(b => b.Classes.Contains("qb-card"));
        Assert.Equal(host.Brush("DbConnectPanelBrush"), ThemedHost.ColorOf(card.Background));

        var title = card.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "customers");
        Assert.Equal(host.Brush("DbConnectContentBrush"), ThemedHost.ColorOf(title.Foreground));

        var join = canvas.Children.OfType<Line>().Single();
        Assert.Equal(host.Brush("DbConnectJoinLeftBrush"), ThemedHost.ColorOf(join.Stroke));
    }

    [AvaloniaFact]
    public void EmptyCanvas_SaysHowToAddATable_UntilOneIsAdded()
    {
        var viewModel = new QueryBuilderViewModel();
        var view = new QueryBuilderView { DataContext = viewModel };
        using var host = ThemedHost.Show(view, "Light", height: 800, width: 1200);

        Assert.Contains(VisibleEmptyStates(view), e => e.Title == "No tables yet");

        AddTable(viewModel, "customers");
        host.Window.UpdateLayout();
        Assert.DoesNotContain(VisibleEmptyStates(view), e => e.Title == "No tables yet");
    }

    private static void AddTable(QueryBuilderViewModel viewModel, string name) =>
        viewModel.AddTableCommand.Execute(new QueryBuilderTable
        {
            TableName = name,
            Columns = [new QueryBuilderColumn { ColumnName = "id" }, new QueryBuilderColumn { ColumnName = "customer_id" }],
        });

    private static IEnumerable<EmptyState> VisibleEmptyStates(Control root) =>
        root.GetVisualDescendants().OfType<EmptyState>().Where(e => e.IsEffectivelyVisible);
}
