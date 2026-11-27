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
/// Issue 11's key controls as the rendered designer shows them: selecting a key shows its ordered
/// members; the Up, Down, Remove and Add buttons and the name cell edit it and refresh the preview;
/// the Columns grid's PK checkboxes follow the primary key and edit it; and a primary-key name the
/// engine fixes is read-only. Semantics against real databases are in <see cref="KeyEditingWorkflowTests"/>.
/// </summary>
public sealed class TableDesignerKeysHeadlessTests
{
    private static readonly DatabaseObjectIdentity Table = new(null, "s", "items", SchemaNodeType.Table);

    private static TableDesignerViewModel Designer(string provider, Func<TableDesign, TableDesign>? adjust = null)
    {
        var design = new TableDesign(Table, provider)
        {
            Columns = [new("a", "integer", false), new("b", "integer", false), new("c", "text", true)],
            PrimaryKey = new TableDesignKey(provider == "MySQL" ? null : "pk_t", ["a", "b"]),
            UniqueKeys = [new TableDesignKey("uq_c", ["c"])],
            Indexes =
            [
                new TableDesignIndex(provider == "MySQL" ? "PRIMARY" : "pk_t", true, [new("a", null, false), new("b", null, false)], TableDesignIndexOrigin.PrimaryKey),
                new TableDesignIndex("uq_c", true, [new("c", null, false)], TableDesignIndexOrigin.UniqueKey)
            ]
        };
        design = adjust?.Invoke(design) ?? design;
        var mock = new Mock<IDatabaseProvider>();
        mock.SetupGet(p => p.ProviderName).Returns(provider);
        mock.SetupGet(p => p.Capabilities).Returns(new DatabaseProviderCapabilities("\"", "\"", "LIMIT")
        {
            TransactionalDdl = true,
            SupportsTableDesignerRenameColumn = provider == "MySQL"
        });
        mock.Setup(p => p.GetSupportedDataTypes()).Returns(["integer", "text"]);
        mock.Setup(p => p.GetColumnDefinitionsAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(() =>
            [
                new ColumnDefinition { Name = "a", DataType = "integer", IsNullable = false, IsPrimaryKey = true },
                new ColumnDefinition { Name = "b", DataType = "integer", IsNullable = false, IsPrimaryKey = true },
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
            view.GetVisualDescendants().OfType<TabItem>().Single(t => Equals(t.Header, "Constraints"));
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

    [AvaloniaFact]
    public async Task SelectingAKeyShowsItsMembers_AndTheButtonsReorderAddAndRenameThroughThePlan()
    {
        var designer = Designer("PostgreSQL");
        var (host, view) = await ShowAsync(designer);
        using var _ = host;
        var grid = view.FindControl<DataGrid>("KeyGrid")!;
        Assert.Equal(["Primary key pk_t a, b", "Unique uq_c c"], designer.Keys.Items.Select(k => $"{k.KindDisplay} {k.Name} {k.ColumnsDisplay}"));
        Assert.Contains("RENAME CONSTRAINT", view.FindControl<TextBlock>("KeyNote")!.Text);
        Assert.False(view.FindControl<Button>("KeyMemberUpButton")!.IsEffectivelyVisible);

        grid.SelectedItem = designer.Keys.Items[0];
        Pump(host);
        var members = view.FindControl<ListBox>("KeyMemberList")!;
        Assert.Equal(["a", "b"], members.Items.Cast<string>());
        members.SelectedItem = "b";
        Pump(host);
        Click(view, "KeyMemberUpButton", host);
        Assert.Equal(["b", "a"], members.Items.Cast<string>());
        Assert.Equal("""
            ALTER TABLE "s"."items" DROP CONSTRAINT "pk_t";
            ALTER TABLE "s"."items" ADD CONSTRAINT "pk_t" PRIMARY KEY ("b", "a");
            """.ReplaceLineEndings().TrimEnd(), view.FindControl<TextBox>("DdlPreview")!.Text);
        Assert.Equal("Changed", designer.Keys.Items[0].Status);
        Click(view, "KeyMemberDownButton", host);
        Assert.Equal("", designer.PreviewSql);

        // Adding a member picked from the table's columns.
        view.FindControl<ComboBox>("KeyMemberColumn")!.SelectedItem = "c";
        Pump(host);
        Click(view, "KeyMemberAddButton", host);
        Assert.Equal(["a", "b", "c"], members.Items.Cast<string>());
        Assert.Contains("Column \"c\" is in the primary key, so it must be Not Null.", designer.ValidationErrors);
        Assert.False(view.FindControl<Button>("SaveButton")!.IsEnabled);
        Assert.True(designer.Columns.Single(c => c.Name == "c").IsPrimaryKey);
        members.SelectedItem = "c";
        Pump(host);
        Click(view, "KeyMemberRemoveButton", host);
        Assert.Empty(designer.ValidationErrors);

        // Renaming the unique key through its name cell.
        grid.SelectedItem = designer.Keys.Items[1];
        grid.CurrentColumn = grid.Columns.Single(c => Equals(c.Header, "Name"));
        Assert.True(grid.BeginEdit());
        Pump(host);
        var editor = grid.GetVisualDescendants().OfType<TextBox>().Single(t => t.DataContext == designer.Keys.Items[1]);
        editor.Text = "uq_c2";
        Assert.True(grid.CommitEdit());
        Pump(host);
        Assert.Equal("""ALTER TABLE "s"."items" RENAME CONSTRAINT "uq_c" TO "uq_c2";""", designer.PreviewSql);
        Assert.Empty(designer.Plan.Destructive);
    }

    [AvaloniaFact]
    public async Task ThePkCheckboxesFollowThePrimaryKey_AndUncheckingTheLastColumnDropsIt()
    {
        var designer = Designer("PostgreSQL");
        var (host, view) = await ShowAsync(designer);
        using var _ = host;
        var pk = designer.Keys.Items[0];
        designer.Columns.Single(c => c.Name == "a").IsPrimaryKey = false;
        Pump(host);
        Assert.Equal(["b"], pk.Columns);
        designer.Columns.Single(c => c.Name == "b").IsPrimaryKey = false;
        Pump(host);
        Assert.True(pk.IsDropped);
        Assert.Equal("Dropped", pk.Status);
        Assert.Equal("""ALTER TABLE "s"."items" DROP CONSTRAINT "pk_t";""", designer.PreviewSql);
        Assert.Contains(designer.Plan.Destructive, d => d.StartsWith("Drops Primary key pk_t"));
        Assert.False(view.FindControl<Button>("SaveButton")!.IsEnabled);

        view.FindControl<DataGrid>("KeyGrid")!.SelectedItem = pk;
        designer.Keys.DropKeyCommand.Execute(pk);   // keep it again
        Pump(host);
        Assert.False(pk.IsDropped);
    }

    [AvaloniaFact]
    public async Task APrimaryKeyNameTheEngineFixes_IsReadOnly()
    {
        var designer = Designer("MySQL");
        var (host, view) = await ShowAsync(designer);
        using var _ = host;
        var grid = view.FindControl<DataGrid>("KeyGrid")!;
        Assert.Contains("always named PRIMARY", view.FindControl<TextBlock>("KeyNote")!.Text);
        foreach (var (key, locked) in new[] { (designer.Keys.Items[0], true), (designer.Keys.Items[1], false) })
        {
            grid.SelectedItem = key;
            grid.CurrentColumn = grid.Columns.Single(c => Equals(c.Header, "Name"));
            Assert.True(grid.BeginEdit());
            Pump(host);
            Assert.Equal(locked, grid.GetVisualDescendants().OfType<TextBox>().Single(t => t.DataContext == key).IsReadOnly);
            grid.CancelEdit();
            Pump(host);
        }
        Assert.Equal("", designer.PreviewSql);
    }

    /// <summary>Review fixes: a cleared name keeps a loaded key's name; a renamed column stays in its keys.</summary>
    [AvaloniaFact]
    public async Task ClearingALoadedKeysName_KeepsIt_AndRenamingAColumnKeepsItsKeys()
    {
        var designer = Designer("MySQL");
        var (host, _) = await ShowAsync(designer);
        using var __ = host;
        var unique = designer.Keys.Items[1];
        unique.Name = "";
        Assert.Equal("", designer.PreviewSql);
        Assert.Equal("", unique.Status);

        designer.Columns.Single(c => c.Name == "a").Name = "a2";
        Pump(host);
        Assert.Equal(["a2", "b"], designer.Keys.Items[0].Columns);
        Assert.True(designer.Columns.Single(c => c.Name == "a2").IsPrimaryKey);
        Assert.Contains("CHANGE `a` `a2`", designer.PreviewSql);
        Assert.DoesNotContain("PRIMARY KEY", designer.PreviewSql);
        Assert.Empty(designer.ValidationErrors);
    }

    /// <summary>MySQL needs an index for each foreign key; the only one cannot be dropped.</summary>
    [AvaloniaFact]
    public async Task AKeyThatIsTheOnlyIndexForAForeignKey_CannotBeDroppedOnMySql()
    {
        var designer = Designer("MySQL", d => d with
        {
            ForeignKeys = [new TableDesignForeignKey("fk_c", [new("c", "code")], new(null, null, "codes", SchemaNodeType.Table), "NO ACTION", "NO ACTION")]
        });
        var (host, _) = await ShowAsync(designer);
        using var __ = host;
        designer.Keys.DropKeyCommand.Execute(designer.Keys.Items[1]);
        Assert.Contains("Unique key uq_c is the only index for foreign key fk_c, which MySQL needs; add an index on its columns first.", designer.Plan.Blocked);
        designer.Keys.DropKeyCommand.Execute(designer.Keys.Items[1]);
        Assert.Empty(designer.Plan.Blocked);
    }
}
