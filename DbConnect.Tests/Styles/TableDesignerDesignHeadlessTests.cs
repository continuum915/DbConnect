using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using Moq;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using DbConnect.Views;
using ColumnDefinition = DbConnect.Models.ColumnDefinition;

namespace DbConnect.Tests.Styles;

/// <summary>
/// The designer's rendering of a loaded table design (issue 06): load states, the read-only tabs and
/// their labels, and Save gating. The full semantic coverage is in the provider workflow tests.
/// </summary>
public sealed class TableDesignerDesignHeadlessTests
{
    private static readonly DatabaseObjectIdentity Table = new(null, "s", "items", SchemaNodeType.Table);

    private static TableDesign Design(string provider = "PostgreSQL") => new(Table, provider)
    {
        Columns =
        [
            new TableDesignColumn("id", "bigint", false) { Identity = new(TableDesignIdentityKind.Always, "1", "1") },
            new TableDesignColumn("name", "text", true) { Collation = "\"C\"" },
            new TableDesignColumn("total", "integer", true) { Generated = new("id * 2", TableDesignGeneratedStorage.Stored) }
        ],
        PrimaryKey = new TableDesignKey("pk_items", ["id"]),
        UniqueKeys = [new TableDesignKey(null, ["name"])],
        ForeignKeys = [new TableDesignForeignKey("fk_self", [new("id", "id")], Table, "CASCADE", "NO ACTION")],
        Checks = [new TableDesignCheck("ck_total", "total > 0") { Column = "total" }],
        Indexes =
        [
            new TableDesignIndex("pk_items", true, [new("id", null, false)], TableDesignIndexOrigin.PrimaryKey),
            new TableDesignIndex("ix_name", false, [new(null, "lower(name)", true)], TableDesignIndexOrigin.CreateIndex)
            {
                IncludedColumns = ["total"], Filter = "name IS NOT NULL", Method = "btree"
            }
        ],
        Triggers = [new TableDesignTrigger("trg", "BEFORE", "INSERT FOR EACH ROW", "CREATE TRIGGER trg ...")],
        Options = [new TableDesignOption("fillfactor", "80")]
    };

    private static (TableDesignerViewModel Designer, Mock<IDatabaseProvider> Provider) Designer(
        Func<Task<TableDesign>>? load, string provider = "PostgreSQL")
    {
        var mock = new Mock<IDatabaseProvider>();
        mock.SetupGet(p => p.ProviderName).Returns(provider);
        mock.SetupGet(p => p.Capabilities).Returns(new DatabaseProviderCapabilities("\"", "\"", "LIMIT"));
        mock.Setup(p => p.GetSupportedDataTypes()).Returns(["integer"]);
        mock.Setup(p => p.GetColumnDefinitionsAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(() => [new ColumnDefinition { Name = "id" }, new ColumnDefinition { Name = "name" }, new ColumnDefinition { Name = "total" }]);
        mock.Setup(p => p.GetIndexesAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(() => [new IndexDefinition { Name = "ix_name", Columns = ["name"] }]);
        mock.Setup(p => p.GetSchemaAsync(It.IsAny<string>())).ReturnsAsync([]);
        if (load != null)
            mock.As<ITableDesignSource>()
                .Setup(p => p.GetTableDesignAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), It.IsAny<CancellationToken>()))
                .Returns(load);
        var session = new DatabaseSession(new ConnectionInfo { Name = "t", Provider = provider }, mock.Object);
        return (new TableDesignerViewModel("items", session, new SchemaNode { Name = "items", Type = SchemaNodeType.Table }, Table), mock);
    }

    private static ThemedHost Show(TableDesignerViewModel designer, out TableDesignerView view)
    {
        view = new TableDesignerView { DataContext = designer };
        return ThemedHost.Show(view, "Light", height: 800, width: 1200);
    }

    private static async Task Settle(ThemedHost host, Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(done(), "The designer did not reach the expected state.");
        HeadlessFrames.Pump(host.Window);
    }

    private static TabItem Tab(TableDesignerView view, string header)
    {
        var tabs = view.GetLogicalDescendants().OfType<TabControl>().Single();
        var tab = tabs.Items.OfType<TabItem>().Single(t => (string?)t.Header == header);
        tabs.SelectedItem = tab;
        return tab;
    }

    [AvaloniaFact]
    public async Task LoadingThenLoaded_ShowsEveryElementOnItsTabAndEnablesSave()
    {
        var gate = new TaskCompletionSource<TableDesign>();
        var (designer, _) = Designer(() => gate.Task);
        using var host = Show(designer, out var view);
        var save = view.FindControl<Button>("SaveButton")!;
        await Settle(host, () => designer.Design.IsLoading);
        Assert.True(view.FindControl<Border>("DesignLoadingBanner")!.IsVisible);
        Assert.False(save.IsEnabled);
        Assert.Contains("until the table design has loaded", view.FindControl<Border>("SaveBlockedBanner")!.GetLogicalDescendants().OfType<TextBlock>().Single().Text);

        gate.SetResult(Design());
        await Settle(host, () => designer.Design.IsLoaded && !designer.IsLoading);
        Assert.False(view.FindControl<Border>("DesignLoadingBanner")!.IsVisible);
        Assert.False(view.FindControl<Border>("SaveBlockedBanner")!.IsVisible);
        Assert.True(save.IsEnabled);

        Assert.Equal("\"C\"", designer.Columns.Single(c => c.Name == "name").Collation);
        var id = designer.Columns.Single(c => c.Name == "id");
        Assert.Equal(("Always", "1", "1"), (id.IdentityLabel, id.IdentitySeed, id.IdentityIncrement));
        var total = designer.Columns.Single(c => c.Name == "total");
        Assert.Equal(("id * 2", "Stored"), (total.GeneratedExpression, total.GeneratedStorageLabel));

        Tab(view, "Indexes");
        HeadlessFrames.Pump(host.Window);
        var index = designer.Indexes.Items.Single(i => i.Name == "ix_name");
        Assert.Equal("(lower(name)) DESC", index.PartsDisplay);
        Assert.Equal(("total", "name IS NOT NULL", "btree"), (index.IncludedDisplay, index.FilterDisplay, index.MethodDisplay));
        var backing = designer.Indexes.Items.Single(i => i.Name == "pk_items");
        Assert.Equal(("Primary key", true, false), (backing.BacksDisplay, backing.IsReadOnly, backing.IsEditable));
        Assert.Equal(2, view.FindControl<DataGrid>("IndexGrid")!.ItemsSource!.Cast<object>().Count());

        Tab(view, "Foreign Keys");
        HeadlessFrames.Pump(host.Window);
        // Loaded foreign keys are edited in the foreign-key grid (issue 12).
        var key = Assert.Single(designer.ForeignKeys.Items);
        Assert.Equal(("fk_self", "id -> id", "s.items", "CASCADE"), (key.Name, key.PairsDisplay, key.TargetDisplay, key.OnDelete));
        Assert.True(view.FindControl<DataGrid>("ForeignKeyGrid")!.IsEffectivelyVisible);

        Tab(view, "Constraints");
        HeadlessFrames.Pump(host.Window);
        // Keys are edited in the key grid (issue 11); the loaded checks stay read-only here.
        Assert.Equal(["Primary key pk_items id", "Unique  name"],
            designer.Keys.Items.Select(k => $"{k.KindDisplay} {k.Name} {k.ColumnsDisplay}"));
        Assert.True(view.FindControl<DataGrid>("KeyGrid")!.IsEffectivelyVisible);
        // Checks are edited in the check grid (issue 13).
        Assert.Equal(["ck_total total > 0 on total"], designer.Checks.Items.Select(c => $"{c.Name} {c.Expression} on {c.Column}"));
        Assert.True(view.FindControl<DataGrid>("CheckGrid")!.IsEffectivelyVisible);

        Tab(view, "Triggers");
        HeadlessFrames.Pump(host.Window);
        Assert.True(view.FindControl<DataGrid>("TriggerGrid")!.IsEffectivelyVisible);
        Assert.Single(view.FindControl<DataGrid>("TriggerGrid")!.ItemsSource!.Cast<object>());

        Tab(view, "Options");
        HeadlessFrames.Pump(host.Window);
        Assert.True(view.FindControl<DataGrid>("OptionGrid")!.IsEffectivelyVisible);
        Assert.Single(view.FindControl<DataGrid>("OptionGrid")!.ItemsSource!.Cast<object>());
    }

    [AvaloniaFact]
    public async Task EngineLimits_AreLabelledNotSupportedInsteadOfEmptyTabs()
    {
        var design = Design("DuckDB") with
        {
            Triggers = [],
            Options = [],
            Unsupported = new Dictionary<TableDesignFeature, string>
            {
                [TableDesignFeature.Triggers] = TableDesign.NotSupportedBy("DuckDB"),
                [TableDesignFeature.Options] = TableDesign.NotSupportedBy("DuckDB"),
                [TableDesignFeature.ConstraintChanges] = TableDesign.NotSupportedBy("DuckDB")
            }
        };
        var (designer, _) = Designer(() => Task.FromResult(design), "DuckDB");
        using var host = Show(designer, out var view);
        await Settle(host, () => designer.Design.IsLoaded && !designer.IsLoading);

        Tab(view, "Triggers");
        HeadlessFrames.Pump(host.Window);
        Assert.Equal("Not supported by DuckDB", view.FindControl<TextBlock>("TriggerNote")!.Text);
        Assert.False(view.FindControl<DataGrid>("TriggerGrid")!.IsVisible);
        Tab(view, "Options");
        HeadlessFrames.Pump(host.Window);
        Assert.Equal("Not supported by DuckDB", view.FindControl<TextBlock>("OptionNote")!.Text);
        Assert.Null(designer.Design.IndexLimits);
        Assert.True(view.FindControl<TextBlock>("ColumnPreservedNote")!.IsVisible);
        Assert.Contains("Changing keys, foreign keys and checks after creation: Not supported by DuckDB", designer.Design.Limits);
        Assert.Equal("Checks of this table cannot be changed after creation: Not supported by DuckDB.", designer.Checks.Summary);
    }

    [AvaloniaFact]
    public async Task FailedLoad_ShowsTheErrorWithRetryAndDisablesSaveUntilRetrySucceeds()
    {
        var attempts = 0;
        var (designer, _) = Designer(() => ++attempts == 1
            ? Task.FromException<TableDesign>(new TableDefinitionUnavailableException("permission denied for pg_trigger"))
            : Task.FromResult(Design()));
        using var host = Show(designer, out var view);
        await Settle(host, () => designer.Design.IsFailed && !designer.IsLoading);
        Assert.True(view.FindControl<Border>("DesignFailedBanner")!.IsVisible);
        Assert.False(view.FindControl<Button>("SaveButton")!.IsEnabled);
        Assert.Contains("permission denied for pg_trigger", designer.Design.SaveBlockedReason);

        designer.Columns.Add(new ColumnDefinition { Name = "added", DataType = "integer", IsNew = true });
        await designer.SaveCommand.ExecuteAsync(null);
        Assert.Contains("could not be loaded", designer.StatusMessage);

        await ((IAsyncRelayCommand)view.FindControl<Button>("RetryDesignButton")!.Command!).ExecuteAsync(null);
        await Settle(host, () => designer.Design.IsLoaded && !designer.IsLoading);
        Assert.False(view.FindControl<Border>("DesignFailedBanner")!.IsVisible);
        Assert.Equal(TableDesignIdentityKind.Always, designer.Columns.Single(c => c.Name == "id").IdentityKind);
        Assert.Contains(designer.Indexes.Items, i => i.Name == "pk_items" && i.IsReadOnly);
        Assert.True(view.FindControl<Button>("SaveButton")!.IsEnabled);
    }

    [AvaloniaFact]
    public async Task ProviderWithoutDesignReader_IsUnavailableAndSaveIsDisabled()
    {
        var (designer, mock) = Designer(load: null);
        using var host = Show(designer, out var view);
        await Settle(host, () => designer.Design.IsUnavailable && !designer.IsLoading);
        Assert.True(view.FindControl<Border>("DesignUnavailableBanner")!.IsVisible);
        Assert.False(view.FindControl<Button>("SaveButton")!.IsEnabled);
        designer.Columns.Add(new ColumnDefinition { Name = "added", DataType = "integer", IsNew = true });
        await designer.SaveCommand.ExecuteAsync(null);
        Assert.Contains("cannot read the complete table design", designer.StatusMessage);
        mock.Verify(p => p.ExecuteNonQueryAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [AvaloniaFact]
    public async Task UnrepresentedStructure_IsListedInABannerAndBlocksSave()
    {
        var design = Design() with { Unrepresented = [new TableDesignGap("This table is partitioned by RANGE (at).")] };
        var (designer, _) = Designer(() => Task.FromResult(design));
        using var host = Show(designer, out var view);
        await Settle(host, () => designer.Design.IsLoaded && !designer.IsLoading);
        Assert.True(view.FindControl<Border>("UnrepresentedBanner")!.IsVisible);
        Assert.Equal(["This table is partitioned by RANGE (at)."], designer.Design.Unrepresented);
        Assert.False(view.FindControl<Button>("SaveButton")!.IsEnabled);
        Assert.True(view.FindControl<Border>("SaveBlockedBanner")!.IsVisible);
        Assert.Equal(3, designer.Columns.Count);
    }

    [AvaloniaFact]
    public void NewTable_HasNothingToLoadAndCanSaveOnceItHasAColumn()
    {
        var mock = new Mock<IDatabaseProvider>();
        mock.SetupGet(p => p.ProviderName).Returns("PostgreSQL");
        mock.SetupGet(p => p.Capabilities).Returns(new DatabaseProviderCapabilities("\"", "\"", "LIMIT"));
        mock.Setup(p => p.GetSupportedDataTypes()).Returns(["integer"]);
        var designer = new TableDesignerViewModel("new_table", new DatabaseSession(new ConnectionInfo(), mock.Object));
        using var host = Show(designer, out var view);
        Assert.Equal(TableDesignLoadState.New, designer.Design.State);
        Assert.False(view.FindControl<Border>("SaveBlockedBanner")!.IsVisible);
        Assert.False(view.FindControl<Button>("SaveButton")!.IsEnabled);
        Assert.Contains("At least one column is required.", designer.ValidationErrors);
        designer.AddColumnCommand.Execute(null);
        Assert.True(view.FindControl<Button>("SaveButton")!.IsEnabled);
        Assert.Empty(designer.ValidationErrors);
    }
}
