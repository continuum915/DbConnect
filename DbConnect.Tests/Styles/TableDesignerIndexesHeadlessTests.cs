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
/// Issue 14's index controls as the rendered designer shows them: selecting an index shows its ordered key
/// parts; the Up, Down, Remove and Add buttons, the Descending and Unique checkboxes and the name cell edit it
/// and refresh the preview; an index backing a key is shown read-only; and a re-creation that would lose a
/// loaded setting is listed with Save disabled. Semantics against real databases are in
/// <see cref="IndexEditingWorkflowTests"/> and the provider conformance tests.
/// </summary>
public sealed class TableDesignerIndexesHeadlessTests
{
    private static readonly DatabaseObjectIdentity Table = new(null, "s", "items", SchemaNodeType.Table);

    private static TableDesignerViewModel Designer(string provider = "PostgreSQL")
    {
        var design = new TableDesign(Table, provider)
        {
            Columns = [new("a", "integer", false), new("b", "integer", true), new("c", "text", true)],
            PrimaryKey = new TableDesignKey("pk_t", ["a"]),
            Indexes =
            [
                new TableDesignIndex("ix_bc", false, [new("b", null, false), new("c", null, true)], TableDesignIndexOrigin.CreateIndex)
                {
                    Filter = "b > 0",
                    Method = "btree"
                },
                new TableDesignIndex("ix_kept", false, [new("c", null, false)], TableDesignIndexOrigin.CreateIndex)
                {
                    Properties = ["fillfactor=70"]
                },
                new TableDesignIndex("pk_t", true, [new("a", null, false)], TableDesignIndexOrigin.PrimaryKey)
            ]
        };
        var mock = new Mock<IDatabaseProvider>();
        mock.SetupGet(p => p.ProviderName).Returns(provider);
        mock.SetupGet(p => p.Capabilities).Returns(new DatabaseProviderCapabilities("\"", "\"", "LIMIT") { TransactionalDdl = true });
        mock.Setup(p => p.GetSupportedDataTypes()).Returns(["integer", "text"]);
        mock.Setup(p => p.GetColumnDefinitionsAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(() =>
            [
                new ColumnDefinition { Name = "a", DataType = "integer", IsNullable = false, IsPrimaryKey = true },
                new ColumnDefinition { Name = "b", DataType = "integer" },
                new ColumnDefinition { Name = "c", DataType = "text" }
            ]);
        mock.Setup(p => p.GetIndexesAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync([]);
        mock.Setup(p => p.GetSchemaAsync(It.IsAny<string>())).ReturnsAsync([]);
        mock.As<ITableDesignSource>()
            .Setup(p => p.GetTableDesignAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(design);
        var session = new DatabaseSession(new ConnectionInfo { Name = "t", Provider = provider }, mock.Object);
        return new TableDesignerViewModel("items", session, new SchemaNode { Name = "items", Type = SchemaNodeType.Table }, Table);
    }

    private static async Task<(ThemedHost Host, TableDesignerView View)> ShowAsync(TableDesignerViewModel designer)
    {
        var view = new TableDesignerView { DataContext = designer };
        var host = ThemedHost.Show(view, "Light", height: 900, width: 1400);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!(designer.Design.IsLoaded && !designer.IsLoading) && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();
            await Task.Delay(10);
        }
        Assert.True(designer.Design.IsLoaded && !designer.IsLoading, "The design did not load.");
        view.GetVisualDescendants().OfType<TabControl>().First().SelectedItem =
            view.GetVisualDescendants().OfType<TabItem>().Single(t => Equals(t.Header, "Indexes"));
        Pump(host);
        return (host, view);
    }

    private static void Pump(ThemedHost host)
    {
        Dispatcher.UIThread.RunJobs();
        host.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(TableDesignerView view, string name, ThemedHost host)
    {
        var button = view.FindControl<Button>(name)!;
        Assert.True(button.IsEffectivelyVisible && button.IsEffectivelyEnabled, $"{name} cannot be clicked.");
        Assert.True(button.Command!.CanExecute(button.CommandParameter), $"{name}'s command cannot run.");
        button.Command.Execute(button.CommandParameter);
        Pump(host);
    }

    private static string Preview(TableDesignerView view) => view.FindControl<TextBox>("DdlPreview")!.Text ?? "";

    /// <summary>The CheckBox in the grid row showing <paramref name="item"/>.</summary>
    private static CheckBox CheckBox(DataGrid grid, object item) =>
        grid.GetVisualDescendants().OfType<DataGridRow>().Single(r => r.IsVisible && ReferenceEquals(r.DataContext, item))
            .GetVisualDescendants().OfType<CheckBox>().Single();

    [AvaloniaFact]
    public async Task SelectingAnIndexShowsItsParts_AndTheControlsReorderFlipAddAndRenameThroughThePlan()
    {
        var designer = Designer();
        var (host, view) = await ShowAsync(designer);
        using var _ = host;
        var grid = view.FindControl<DataGrid>("IndexGrid")!;
        Assert.Equal(["ix_bc b, c DESC", "ix_kept c", "pk_t a"], designer.Indexes.Items.Select(i => $"{i.Name} {i.PartsDisplay}"));
        Assert.Contains("ALTER INDEX", view.FindControl<TextBlock>("IndexNote")!.Text);
        Assert.False(view.FindControl<Button>("IndexPartUpButton")!.IsEffectivelyVisible);

        grid.SelectedItem = designer.Indexes.Items[0];
        Pump(host);
        var parts = view.FindControl<DataGrid>("IndexPartGrid")!;
        Assert.Equal(["b", "c"], parts.ItemsSource!.Cast<EditableIndexPart>().Select(p => p.KeyText));
        parts.SelectedItem = designer.Indexes.Items[0].Parts[1];
        Pump(host);
        Click(view, "IndexPartUpButton", host);
        Assert.Equal("""
            DROP INDEX "s"."ix_bc";
            CREATE INDEX "ix_bc" ON "s"."items" ("c" DESC, "b") WHERE b > 0;
            """.ReplaceLineEndings().TrimEnd(), Preview(view));
        Assert.Equal("Changed", designer.Indexes.Items[0].Status);
        Assert.Single(designer.Plan.Destructive);

        // The Descending checkbox of the first part flips it back to ascending.
        var first = designer.Indexes.Items[0].Parts[0];
        Assert.Equal([true, false], designer.Indexes.Items[0].Parts.Select(p => CheckBox(parts, p).IsChecked == true));
        CheckBox(parts, first).IsChecked = false;
        Pump(host);
        Assert.Contains("(\"c\", \"b\") WHERE b > 0", Preview(view));

        // Back to the loaded order and direction, then add a column with the picker.
        CheckBox(parts, first).IsChecked = true;
        Click(view, "IndexPartDownButton", host);
        Assert.Equal("", Preview(view));
        view.FindControl<ComboBox>("IndexPartColumn")!.SelectedItem = "a";
        Pump(host);
        Click(view, "IndexPartAddButton", host);
        Assert.Contains("(\"b\", \"c\" DESC, \"a\") WHERE b > 0", Preview(view));
        parts.SelectedItem = designer.Indexes.Items[0].Parts[2];
        Pump(host);
        Click(view, "IndexPartRemoveButton", host);
        Assert.Equal("", Preview(view));

        // The Unique checkbox re-creates it; a new name alone renames it in place.
        CheckBox(grid, designer.Indexes.Items[0]).IsChecked = true;
        Pump(host);
        Assert.Contains("CREATE UNIQUE INDEX \"ix_bc\"", Preview(view));
        CheckBox(grid, designer.Indexes.Items[0]).IsChecked = false;
        designer.Indexes.Items[0].Name = "ix_bc2";
        Pump(host);
        Assert.Equal("ALTER INDEX \"s\".\"ix_bc\" RENAME TO \"ix_bc2\";", Preview(view));
        Assert.Empty(designer.Plan.Destructive);
        Assert.True(designer.PlanAllowsSave);
    }

    [AvaloniaFact]
    public async Task AnIndexBackingAKeyIsReadOnly_AndALossyReCreationIsListedWithSaveDisabled()
    {
        var designer = Designer();
        var (host, view) = await ShowAsync(designer);
        using var _ = host;
        var grid = view.FindControl<DataGrid>("IndexGrid")!;

        var backing = designer.Indexes.Items[2];
        Assert.Equal(("Primary key", false), (backing.BacksDisplay, backing.IsEditable));
        grid.SelectedItem = backing;
        Pump(host);
        Assert.True(view.FindControl<TextBlock>("IndexBacksKeyNote")!.IsEffectivelyVisible);
        Assert.False(view.FindControl<Button>("IndexPartUpButton")!.IsEffectivelyEnabled);
        Assert.False(CheckBox(grid, backing).IsEffectivelyEnabled);
        designer.Indexes.DropIndexCommand.Execute(backing);
        Assert.False(backing.IsDropped);

        // ix_kept has a fill factor the designer cannot restate, so it cannot be re-created; dropping it is reviewed.
        CheckBox(grid, designer.Indexes.Items[1]).IsChecked = true;
        Pump(host);
        Assert.Contains("Index ix_kept cannot be re-created without losing its fillfactor=70.", designer.ValidationErrors);
        Assert.False(view.FindControl<Button>("SaveButton")!.IsEnabled);
        CheckBox(grid, designer.Indexes.Items[1]).IsChecked = false;
        designer.Indexes.DropIndexCommand.Execute(designer.Indexes.Items[1]);
        Pump(host);
        Assert.Equal("DROP INDEX \"s\".\"ix_kept\";", Preview(view));
        Assert.Equal("Dropped", designer.Indexes.Items[1].Status);
        Assert.True(designer.Review.NeedsConfirmation);

        // A renamed column is carried into the index, which the engine also does: it is not re-created or shown as changed.
        designer.Indexes.DropIndexCommand.Execute(designer.Indexes.Items[1]);
        designer.Columns.Single(c => c.Name == "b").Name = "b2";
        Pump(host);
        Assert.Equal("b2, c DESC", designer.Indexes.Items[0].PartsDisplay);
        Assert.Equal("", designer.Indexes.Items[0].Status);
        Assert.DoesNotContain("ix_bc", Preview(view));

        // The Index button adds a row, which is listed until it has a name and columns.
        Click(view, "AddIndexButton", host);
        Assert.Same(designer.Indexes.Items[^1], designer.Indexes.Selected);
        Assert.Contains("Index () has no name.", designer.ValidationErrors);
        Assert.Contains("Index () has no key columns.", designer.ValidationErrors);
    }

    /// <summary>The TextBox in the parts grid row showing <paramref name="part"/> (an expression part).</summary>
    private static TextBox ExpressionBox(DataGrid grid, EditableIndexPart part) =>
        grid.GetVisualDescendants().OfType<DataGridRow>().Single(r => r.IsVisible && ReferenceEquals(r.DataContext, part))
            .GetVisualDescendants().OfType<TextBox>().Single();

    [AvaloniaFact]
    public async Task TheFilterIncludedColumnsMethodAndExpressionControlsEditTheSelectedIndexThroughThePlan()
    {
        var designer = Designer();
        var (host, view) = await ShowAsync(designer);
        using var _ = host;
        view.FindControl<DataGrid>("IndexGrid")!.SelectedItem = designer.Indexes.Items[0];
        Pump(host);
        var bc = designer.Indexes.Items[0];

        // The filter box re-creates the index with the new predicate; restoring it leaves nothing to do.
        var filter = view.FindControl<TextBox>("IndexFilterText")!;
        Assert.Equal("b > 0", filter.Text);
        filter.Text = "b > 1";
        Pump(host);
        Assert.Contains("CREATE INDEX \"ix_bc\" ON \"s\".\"items\" (\"b\", \"c\" DESC) WHERE b > 1;", Preview(view));
        Assert.Equal(("Changed", "b > 1"), (bc.Status, bc.FilterDisplay));
        filter.Text = "b > 0";
        Pump(host);
        Assert.Equal("", Preview(view));

        // The column picker and Include button add an included column; the list and Remove take it out again.
        view.FindControl<ComboBox>("IndexIncludedColumn")!.SelectedItem = "a";
        Pump(host);
        Click(view, "IndexIncludedAddButton", host);
        Assert.Contains("(\"b\", \"c\" DESC) INCLUDE (\"a\") WHERE b > 0;", Preview(view));
        Assert.Equal("a", bc.IncludedDisplay);
        var included = view.FindControl<ListBox>("IndexIncludedList")!;
        Assert.Equal(["a"], included.ItemsSource!.Cast<string>());
        included.SelectedItem = "a";
        Pump(host);
        Click(view, "IndexIncludedRemoveButton", host);
        Assert.Equal("", Preview(view));

        // The method list offers PostgreSQL's methods; hash cannot have two key parts or a descending one.
        var method = view.FindControl<ComboBox>("IndexMethod")!;
        Assert.Equal("btree", method.SelectedItem);
        method.SelectedItem = "hash";
        Pump(host);
        Assert.Contains("Index ix_bc has more than one key part, which hash indexes cannot have.", designer.ValidationErrors);
        Assert.Contains("Index ix_bc has a descending key, which hash indexes cannot have.", designer.ValidationErrors);
        Assert.False(view.FindControl<Button>("SaveButton")!.IsEnabled);
        method.SelectedItem = "btree";
        Pump(host);
        Assert.Equal("", Preview(view));

        // The expression box adds an expression key part, whose text is then edited in the parts grid.
        view.FindControl<TextBox>("IndexExpressionText")!.Text = "lower(c)";
        Pump(host);
        Click(view, "IndexExpressionAddButton", host);
        Assert.Contains("(\"b\", \"c\" DESC, (lower(c))) WHERE b > 0;", Preview(view));
        var parts = view.FindControl<DataGrid>("IndexPartGrid")!;
        Pump(host);
        ExpressionBox(parts, bc.Parts[2]).Text = "upper(c)";
        Pump(host);
        Assert.Contains("(\"b\", \"c\" DESC, (upper(c))) WHERE b > 0;", Preview(view));
        Assert.True(designer.PlanAllowsSave || designer.Review.NeedsConfirmation);

        // An index backing a key: the filter is read-only, and the method and expression controls are disabled.
        view.FindControl<DataGrid>("IndexGrid")!.SelectedItem = designer.Indexes.Items[2];
        Pump(host);
        Assert.True(filter.IsReadOnly);
        Assert.False(method.IsEffectivelyEnabled);
        Assert.False(view.FindControl<Button>("IndexExpressionAddButton")!.IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public async Task AnEngineWithoutFiltersIncludedColumnsOrMethodsShowsOnlyTheExpressionControls()
    {
        var designer = Designer("DuckDB");
        var (host, view) = await ShowAsync(designer);
        using var _ = host;
        view.FindControl<DataGrid>("IndexGrid")!.SelectedItem = designer.Indexes.Items[1];
        Pump(host);

        Assert.True(view.FindControl<TextBox>("IndexExpressionText")!.IsEffectivelyVisible);
        Assert.False(view.FindControl<TextBox>("IndexFilterText")!.IsEffectivelyVisible);
        Assert.False(view.FindControl<ListBox>("IndexIncludedList")!.IsEffectivelyVisible);
        Assert.False(view.FindControl<ComboBox>("IndexMethod")!.IsEffectivelyVisible);
    }
}
