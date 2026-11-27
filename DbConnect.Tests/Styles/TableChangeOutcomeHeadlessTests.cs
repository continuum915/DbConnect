using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Moq;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using DbConnect.Views;
using ColumnDefinition = DbConnect.Models.ColumnDefinition;

namespace DbConnect.Tests.Styles;

/// <summary>
/// The designer's review and outcome UI (issue 08), with a fake engine that fails precisely: the
/// commit boundary before saving, a save that stops part-way, and one whose commit is lost. The
/// edits stay and Save waits for a reload. Real rollback is covered by the workflow tests.
/// </summary>
public sealed class TableChangeOutcomeHeadlessTests
{
    private static readonly DatabaseObjectIdentity Table = new("shop", null, "items", SchemaNodeType.Table);

    private static (TableDesignerViewModel Designer, Mock<IDatabaseProvider> Provider) Designer(bool transactionalDdl)
    {
        var mock = new Mock<IDatabaseProvider>();
        mock.SetupGet(p => p.ProviderName).Returns("MySQL");
        mock.SetupGet(p => p.Capabilities).Returns(new DatabaseProviderCapabilities("`", "`", "LIMIT")
        {
            SupportsObjectComments = true,
            TransactionalDdl = transactionalDdl
        });
        mock.Setup(p => p.GetSupportedDataTypes()).Returns(["int"]);
        mock.Setup(p => p.GetColumnDefinitionsAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(() => [new ColumnDefinition { Name = "id", DataType = "int", IsNullable = false }]);
        mock.Setup(p => p.GetIndexesAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync([]);
        mock.Setup(p => p.GetSchemaAsync(It.IsAny<string>())).ReturnsAsync([]);
        mock.As<ITableDesignSource>()
            .Setup(p => p.GetTableDesignAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TableDesign(Table, "MySQL") { Columns = [new TableDesignColumn("id", "int", false)] });
        var session = new DatabaseSession(new ConnectionInfo { Name = "shop", Provider = "MySQL" }, mock.Object);
        return (new TableDesignerViewModel("items", session, new SchemaNode { Name = "items", Type = SchemaNodeType.Table }, Table), mock);
    }

    private static async Task<ThemedHost> ShowLoadedAsync(TableDesignerViewModel designer, TableDesignerView view)
    {
        var host = ThemedHost.Show(view, "Light", height: 800, width: 1200);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!(designer.Design.IsLoaded && !designer.IsLoading) && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.True(designer.Design.IsLoaded);
        return host;
    }

    private static void Edit(TableDesignerViewModel designer)
    {
        designer.Columns.Add(new ColumnDefinition { Name = "added", DataType = "int", IsNew = true });
        designer.TableComment = "stock";
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task ASaveThatStopsPartWay_ListsEachStepKeepsTheEditsAndWaitsForAReload()
    {
        var (designer, mock) = Designer(transactionalDdl: false);
        mock.SetupSequence(p => p.ExecuteTransactionAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>()))
            .ReturnsAsync(TransactionResult.Success(0))
            .ReturnsAsync(new TransactionResult { Outcome = TransactionOutcome.RolledBack, ErrorMessage = "Comment too long for table 'items'" });
        var view = new TableDesignerView { DataContext = designer };
        using var host = await ShowLoadedAsync(designer, view);
        Edit(designer);
        Assert.True(view.FindControl<Border>("CommitBoundaryBanner")!.IsVisible);
        Assert.Contains("commits each schema change as it runs", designer.Review.CommitBoundary);

        await designer.SaveCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("❌ Save failed part-way: 1 of 2 steps were applied and remain. Comment too long for table 'items'", designer.StatusMessage);
        Assert.Equal(["Applied", "Failed"], designer.Review.Steps.Select(s => s.Status));
        Assert.True(view.FindControl<Border>("OutcomePanel")!.IsVisible);
        Assert.True(view.FindControl<Border>("ReloadRequiredBanner")!.IsVisible);
        Assert.False(view.FindControl<Button>("SaveButton")!.IsEnabled);
        Assert.Contains(designer.Columns, c => c.Name == "added");
        Assert.Equal("stock", designer.TableComment);

        await designer.SaveCommand.ExecuteAsync(null);
        Assert.Contains("Part of the last save may have been applied", designer.StatusMessage);
        mock.Verify(p => p.ExecuteTransactionAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>()), Times.Exactly(2));

        await designer.ReloadCommand.ExecuteAsync(null);
        Assert.False(designer.Review.RequiresReload);
    }

    [AvaloniaFact]
    public async Task ASaveWhoseCommitIsLost_IsReportedAsUncertain()
    {
        var (designer, mock) = Designer(transactionalDdl: true);
        mock.Setup(p => p.ExecuteTransactionAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>()))
            .ReturnsAsync(new TransactionResult
            {
                Outcome = TransactionOutcome.Unknown,
                ErrorMessage = "Commit: connection reset by peer",
                BoundaryErrorMessage = "Commit: connection reset by peer"
            });
        var view = new TableDesignerView { DataContext = designer };
        using var host = await ShowLoadedAsync(designer, view);
        Edit(designer);
        Assert.False(view.FindControl<Border>("CommitBoundaryBanner")!.IsVisible);

        await designer.SaveCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.StartsWith("❌ Save failed and it is unknown whether some steps were applied", designer.StatusMessage);
        Assert.Equal(["Unknown", "Unknown"], designer.Review.Steps.Select(s => s.Status));
        Assert.True(designer.Review.RequiresReload);
        Assert.False(view.FindControl<Button>("SaveButton")!.IsEnabled);
        var steps = view.FindControl<ItemsControl>("OutcomeSteps")!.GetLogicalDescendants().OfType<TextBlock>().ToList();
        Assert.NotEmpty(steps);
    }
}
