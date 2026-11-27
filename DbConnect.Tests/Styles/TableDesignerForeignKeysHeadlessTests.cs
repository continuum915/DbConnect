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
/// Issue 12's foreign-key controls as the rendered designer shows them: the referenced table is picked
/// from the catalog, pairs are edited and reordered in the pair grid with the preview following, a
/// failed table listing or a referenced table whose columns cannot be read shows its reason with a retry
/// (never an empty, valid-looking list), and invalid pairs are listed with Save disabled. Semantics
/// against real databases are in <see cref="ForeignKeyEditingWorkflowTests"/>.
/// </summary>
public sealed class TableDesignerForeignKeysHeadlessTests
{
    private static readonly DatabaseObjectIdentity Table = new(null, "s", "items", SchemaNodeType.Table);

    private static TableDesign Items() => new(Table, "PostgreSQL")
    {
        Columns = [new("id", "integer", false), new("pa", "integer", true), new("pb", "integer", true)],
        PrimaryKey = new TableDesignKey("pk_items", ["id"]),
        Indexes = [new TableDesignIndex("pk_items", true, [new("id", null, false)], TableDesignIndexOrigin.PrimaryKey)]
    };

    private static TableDesign Parent() => new(new(null, "s", "parent", SchemaNodeType.Table), "PostgreSQL")
    {
        Columns = [new("a", "integer", false), new("b", "integer", false)],
        PrimaryKey = new TableDesignKey("pk_parent", ["a", "b"])
    };

    private sealed class Catalog
    {
        public int SchemaCalls;
        public bool FailListing;
        public bool FailParent;
    }

    private static (TableDesignerViewModel Designer, Catalog Catalog) Designer(bool failListing = false, bool failParent = false)
    {
        var catalog = new Catalog { FailListing = failListing, FailParent = failParent };
        var mock = new Mock<IDatabaseProvider>();
        mock.SetupGet(p => p.ProviderName).Returns("PostgreSQL");
        mock.SetupGet(p => p.Capabilities).Returns(new DatabaseProviderCapabilities("\"", "\"", "LIMIT") { TransactionalDdl = true });
        mock.Setup(p => p.GetSupportedDataTypes()).Returns(["integer"]);
        mock.Setup(p => p.GetColumnDefinitionsAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(() =>
            [
                new ColumnDefinition { Name = "id", DataType = "integer", IsNullable = false, IsPrimaryKey = true },
                new ColumnDefinition { Name = "pa", DataType = "integer" },
                new ColumnDefinition { Name = "pb", DataType = "integer" }
            ]);
        mock.Setup(p => p.GetIndexesAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync([]);
        mock.Setup(p => p.GetSchemaAsync(It.IsAny<string>())).Returns(() =>
        {
            catalog.SchemaCalls++;
            if (catalog.FailListing) return Task.FromException<List<SchemaNode>>(new InvalidOperationException("permission denied for schema s"));
            var schema = new SchemaNode { Name = "s", RawName = "s", Type = SchemaNodeType.Schema };
            foreach (var name in new[] { "items", "parent" })
                schema.Children.Add(new SchemaNode { Name = name, RawName = name, Type = SchemaNodeType.Table, SchemaName = "s" });
            return Task.FromResult(new List<SchemaNode> { schema });
        });
        mock.As<ITableDesignSource>()
            .Setup(p => p.GetTableDesignAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), It.IsAny<CancellationToken>()))
            .Returns((string _, DatabaseObjectIdentity table, CancellationToken _) => table.Name switch
            {
                "items" => Task.FromResult(Items()),
                _ when catalog.FailParent => Task.FromException<TableDesign>(new TableDefinitionUnavailableException("permission denied for table parent")),
                _ => Task.FromResult(Parent())
            });
        var session = new DatabaseSession(new ConnectionInfo { Name = "t", Provider = "PostgreSQL" }, mock.Object);
        return (new TableDesignerViewModel("items", session, new SchemaNode { Name = "items", Type = SchemaNodeType.Table }, Table), catalog);
    }

    private static async Task<(ThemedHost Host, TableDesignerView View)> ShowAsync(TableDesignerViewModel designer)
    {
        var view = new TableDesignerView { DataContext = designer };
        var host = ThemedHost.Show(view, "Light", height: 900, width: 1400);
        await Until(host, () => designer.Design.IsLoaded && !designer.IsLoading
                                && designer.ForeignKeys.TargetsState is MetadataLoadState.Loaded or MetadataLoadState.Failed);
        view.GetVisualDescendants().OfType<TabControl>().First().SelectedItem =
            view.GetVisualDescendants().OfType<TabItem>().Single(t => Equals(t.Header, "Foreign Keys"));
        Pump(host);
        return (host, view);
    }

    private static async Task Until(ThemedHost host, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Pump(host);
            await Task.Delay(10);
        }
        Assert.True(condition(), "Timed out.");
        Pump(host);
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
        button.Command!.Execute(button.CommandParameter);
        Pump(host);
    }

    /// <summary>Picks an item in a pair cell through the pair grid's own editor.</summary>
    private static void EditPair(DataGrid grid, ForeignKeyPair pair, string header, string value, ThemedHost host)
    {
        grid.SelectedItem = pair;
        grid.CurrentColumn = grid.Columns.Single(c => Equals(c.Header, header));
        Assert.True(grid.BeginEdit());
        Pump(host);
        var editor = grid.GetVisualDescendants().OfType<ComboBox>().Single(c => c.DataContext == pair);
        Assert.Contains(value, editor.Items.Cast<string>());
        editor.SelectedItem = value;
        Assert.True(grid.CommitEdit());
        Pump(host);
    }

    [AvaloniaFact]
    public async Task TheTargetIsPickedFromTheCatalog_AndPairsAreEditedAndOrderedInTheGrid()
    {
        var (designer, _) = Designer();
        var (host, view) = await ShowAsync(designer);
        using var _ = host;
        Assert.Contains("RENAME CONSTRAINT", view.FindControl<TextBlock>("ForeignKeyNote")!.Text);
        Assert.False(view.FindControl<ComboBox>("ReferencedTablePicker")!.IsEffectivelyVisible);

        Click(view, "AddForeignKeyButton", host);
        var picker = view.FindControl<ComboBox>("ReferencedTablePicker")!;
        Assert.Equal(["s.items", "s.parent"], picker.Items.Cast<ReferenceTarget>().Select(t => t.ToString()));
        picker.SelectedItem = picker.Items.Cast<ReferenceTarget>().Single(t => t.Identity.Name == "parent");
        await Until(host, () => designer.ForeignKeys.ReferencedColumns.Count == 2);

        var fk = designer.ForeignKeys.Selected!;
        var pairs = view.FindControl<DataGrid>("ForeignKeyPairGrid")!;
        EditPair(pairs, fk.Pairs[0], "Column", "pb", host);
        EditPair(pairs, fk.Pairs[0], "Referenced column", "b", host);
        Click(view, "AddPairButton", host);
        EditPair(pairs, fk.Pairs[1], "Column", "pa", host);
        EditPair(pairs, fk.Pairs[1], "Referenced column", "a", host);
        Assert.Contains("""FOREIGN KEY ("pb", "pa") REFERENCES "s"."parent" ("b", "a")""", designer.PreviewSql);
        Assert.Empty(designer.ValidationErrors);

        // Re-ordering the pairs re-orders both sides of the key.
        pairs.SelectedItem = fk.Pairs[1];
        Pump(host);
        Click(view, "PairUpButton", host);
        Assert.Equal("pa -> a, pb -> b", fk.PairsDisplay);
        Assert.Contains("""FOREIGN KEY ("pa", "pb") REFERENCES "s"."parent" ("a", "b")""", designer.PreviewSql);

        // A pair the referenced table cannot satisfy is listed and disables Save.
        Click(view, "RemovePairButton", host);
        Assert.Contains("Foreign key (pb): (b) is not a primary or unique key of s.parent.", designer.ValidationErrors);
        Assert.False(view.FindControl<Button>("SaveButton")!.IsEnabled);
    }

    [AvaloniaFact]
    public async Task AFailedTableListing_ShowsItsReasonWithARetry_NotAnEmptyList()
    {
        var (designer, catalog) = Designer(failListing: true);
        var (host, view) = await ShowAsync(designer);
        using var _ = host;
        Click(view, "AddForeignKeyButton", host);
        Assert.True(view.FindControl<Border>("ReferenceTargetsFailedBanner")!.IsEffectivelyVisible);
        Assert.StartsWith("Metadata could not be loaded.", designer.ForeignKeys.TargetsError);
        Assert.Empty(designer.ForeignKeys.Targets);
        Assert.Contains("Foreign key () has no referenced table.", designer.ValidationErrors);

        catalog.FailListing = false;
        designer.Session!.Catalog.Invalidate(MetadataScope.Root);
        Click(view, "RetryReferenceTargetsButton", host);
        await Until(host, () => designer.ForeignKeys.TargetsState == MetadataLoadState.Loaded);
        Assert.False(view.FindControl<Border>("ReferenceTargetsFailedBanner")!.IsEffectivelyVisible);
        Assert.Contains(designer.ForeignKeys.Targets, t => t.Identity.Name == "parent");
    }

    [AvaloniaFact]
    public async Task AReferencedTableWhoseColumnsCannotBeRead_IsReportedWithARetry_AndBlocksSave()
    {
        var (designer, catalog) = Designer(failParent: true);
        var (host, view) = await ShowAsync(designer);
        using var _ = host;
        Click(view, "AddForeignKeyButton", host);
        var fk = designer.ForeignKeys.Selected!;
        fk.Pairs[0].Column = "pa";
        fk.Pairs[0].ReferencedColumn = "a";
        designer.ForeignKeys.SelectedTarget = designer.ForeignKeys.Targets.Single(t => t.Identity.Name == "parent");
        await Until(host, () => designer.ForeignKeys.TargetColumnsFailed);
        Assert.True(view.FindControl<Border>("TargetColumnsFailedBanner")!.IsEffectivelyVisible);
        Assert.Contains(designer.ValidationErrors, e => e.Contains("could not be read (permission denied for table parent)"));
        Assert.Empty(designer.ForeignKeys.ReferencedColumns);
        Assert.False(view.FindControl<Button>("SaveButton")!.IsEnabled);

        catalog.FailParent = false;
        Click(view, "RetryTargetColumnsButton", host);
        await Until(host, () => !designer.ForeignKeys.TargetColumnsFailed && designer.ForeignKeys.ReferencedColumns.Count == 2);
        Assert.Contains("Foreign key (pa): (a) is not a primary or unique key of s.parent.", designer.ValidationErrors);
    }
}
