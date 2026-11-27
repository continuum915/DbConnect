using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Moq;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using DbConnect.Views;
using ColumnDefinition = DbConnect.Models.ColumnDefinition;

namespace DbConnect.Tests.Styles;

/// <summary>
/// Issue 13's check grid as the rendered designer shows it: loaded checks with their column and state,
/// the Name and Expression cells editing the plan, a malformed expression listed with Save disabled, and
/// the Drop button dropping and keeping a check again. Semantics against real databases are in
/// <see cref="CheckEditingWorkflowTests"/>.
/// </summary>
public sealed class TableDesignerChecksHeadlessTests
{
    private static readonly DatabaseObjectIdentity Table = new(null, "s", "items", SchemaNodeType.Table);

    private static TableDesignerViewModel Designer(string provider = "PostgreSQL")
    {
        var design = new TableDesign(Table, provider)
        {
            Columns = [new("id", "integer", false), new("qty", "integer", true)],
            PrimaryKey = new TableDesignKey("pk_items", ["id"]),
            Checks =
            [
                new TableDesignCheck("ck_qty", "qty >= 0") { Column = "qty" },
                new TableDesignCheck("ck_nv", "qty <> 7") { Column = "qty", State = "NOT VALID" }
            ],
            Indexes = [new TableDesignIndex("pk_items", true, [new("id", null, false)], TableDesignIndexOrigin.PrimaryKey)]
        };
        var mock = new Mock<IDatabaseProvider>();
        mock.SetupGet(p => p.ProviderName).Returns(provider);
        mock.SetupGet(p => p.Capabilities).Returns(new DatabaseProviderCapabilities("\"", "\"", "LIMIT")
        {
            TransactionalDdl = true,
            SupportsTableDesignerDropColumn = true
        });
        mock.Setup(p => p.GetSupportedDataTypes()).Returns(["integer"]);
        mock.Setup(p => p.GetColumnDefinitionsAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(() =>
            [
                new ColumnDefinition { Name = "id", DataType = "integer", IsNullable = false, IsPrimaryKey = true },
                new ColumnDefinition { Name = "qty", DataType = "integer" }
            ]);
        mock.Setup(p => p.GetIndexesAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync([]);
        mock.Setup(p => p.GetSchemaAsync(It.IsAny<string>())).ReturnsAsync([]);
        mock.As<ITableDesignSource>()
            .Setup(p => p.GetTableDesignAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(design);
        var session = new DatabaseSession(new ConnectionInfo { Name = "t", Provider = provider }, mock.Object);
        return new TableDesignerViewModel("items", session, new SchemaNode { Name = "items", Type = SchemaNodeType.Table }, Table);
    }

    private static void Pump(ThemedHost host)
    {
        Dispatcher.UIThread.RunJobs();
        host.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static DataGridRow Row(DataGrid grid, EditableCheck check) =>
        grid.GetVisualDescendants().OfType<DataGridRow>().Single(r => r.DataContext == check);

    private static void EditCell(DataGrid grid, EditableCheck check, string header, string text, ThemedHost host)
    {
        grid.SelectedItem = check;
        grid.CurrentColumn = grid.Columns.Single(c => Equals(c.Header, header));
        Assert.True(grid.BeginEdit());
        Pump(host);
        Row(grid, check).GetVisualDescendants().OfType<TextBox>().Single().Text = text;
        Assert.True(grid.CommitEdit());
        Pump(host);
    }

    [AvaloniaFact]
    public async Task TheCheckGridShowsLoadedChecks_AndItsCellsAndButtonsEditThePlan()
    {
        var designer = Designer();
        var view = new TableDesignerView { DataContext = designer };
        using var host = ThemedHost.Show(view, "Light", height: 900, width: 1400);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!(designer.Design.IsLoaded && !designer.IsLoading) && DateTime.UtcNow < deadline)
        {
            Pump(host);
            await Task.Delay(10);
        }
        view.GetVisualDescendants().OfType<TabControl>().First().SelectedItem =
            view.GetVisualDescendants().OfType<TabItem>().Single(t => Equals(t.Header, "Constraints"));
        Pump(host);
        var grid = view.FindControl<DataGrid>("CheckGrid")!;
        Assert.Contains("RENAME CONSTRAINT", view.FindControl<TextBlock>("CheckNote")!.Text);
        var qty = designer.Checks.Items.Single(c => c.Name == "ck_qty");
        var notValid = designer.Checks.Items.Single(c => c.Name == "ck_nv");
        Assert.Contains("NOT VALID", Row(grid, notValid).GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));

        EditCell(grid, qty, "Expression", "qty >= 1", host);
        Assert.Equal("""
            ALTER TABLE "s"."items" DROP CONSTRAINT "ck_qty";
            ALTER TABLE "s"."items" ADD CONSTRAINT "ck_qty" CHECK (qty >= 1);
            """.ReplaceLineEndings().TrimEnd(), view.FindControl<TextBox>("DdlPreview")!.Text);
        Assert.Equal("Changed", qty.Status);
        EditCell(grid, qty, "Expression", "qty >= 0", host);
        EditCell(grid, qty, "Name", "ck_qty2", host);
        Assert.Equal("""ALTER TABLE "s"."items" RENAME CONSTRAINT "ck_qty" TO "ck_qty2";""", designer.PreviewSql);

        // A malformed expression is listed and disables Save; a stateful check cannot be re-created.
        EditCell(grid, notValid, "Expression", "qty <> 8 -- trailing", host);
        Assert.Contains("Check ck_nv: the expression contains a comment.", designer.ValidationErrors);
        Assert.Contains("Check ck_nv cannot be re-created without losing its NOT VALID.", designer.ValidationErrors);
        Assert.False(view.FindControl<Button>("SaveButton")!.IsEnabled);
        EditCell(grid, notValid, "Expression", "qty <> 7", host);

        // Drop, then keep again, through the row's button.
        var drop = Row(grid, notValid).GetVisualDescendants().OfType<Button>().Single();
        drop.Command!.Execute(drop.CommandParameter);
        Pump(host);
        Assert.Contains("""ALTER TABLE "s"."items" DROP CONSTRAINT "ck_nv";""", designer.PreviewSql);
        Assert.Contains(designer.Plan.Destructive, d => d.StartsWith("Drops Check ck_nv"));
        drop.Command!.Execute(drop.CommandParameter);
        Pump(host);
        Assert.False(notValid.IsDropped);

        view.FindControl<Button>("AddCheckButton")!.Command!.Execute(null);
        Pump(host);
        Assert.Contains("Check () has no expression.", designer.ValidationErrors);
    }

    /// <summary>
    /// Review fixes on MySQL, which re-creates a check to rename it: renaming a NOT ENFORCED check is blocked
    /// rather than silently enforcing it, and a re-created check whose old expression names a dropped column is
    /// dropped before the column, not replaced after it.
    /// </summary>
    [AvaloniaFact]
    public async Task OnMySql_ARenameThatReCreatesKeepsStateSafe_AndAnOldExpressionGoesBeforeItsColumn()
    {
        var designer = Designer("MySQL");
        var view = new TableDesignerView { DataContext = designer };
        using var host = ThemedHost.Show(view, "Light", height: 900, width: 1400);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!(designer.Design.IsLoaded && !designer.IsLoading) && DateTime.UtcNow < deadline)
        {
            Pump(host);
            await Task.Delay(10);
        }
        var notValid = designer.Checks.Items.Single(c => c.Name == "ck_nv");
        notValid.Name = "ck_nv2";
        Assert.Contains("Check ck_nv2 cannot be re-created without losing its NOT VALID.", designer.Plan.Blocked);
        notValid.Name = "ck_nv";
        designer.Checks.DropCheckCommand.Execute(notValid);

        designer.Checks.Items.Single(c => c.Name == "ck_qty").Expression = "id > 0";
        designer.DropColumnCommand.Execute(designer.Columns.Single(c => c.Name == "qty"));
        var steps = designer.Plan.Steps;
        var dropCheck = steps.ToList().FindIndex(s => s.Contains("DROP CHECK `ck_qty`"));
        var dropColumn = steps.ToList().FindIndex(s => s.Contains("DROP COLUMN `qty`"));
        var addCheck = steps.ToList().FindIndex(s => s.Contains("ADD CONSTRAINT `ck_qty` CHECK (id > 0)"));
        Assert.True(dropCheck >= 0 && dropCheck < dropColumn && dropColumn < addCheck, string.Join(Environment.NewLine, steps));
    }
}
