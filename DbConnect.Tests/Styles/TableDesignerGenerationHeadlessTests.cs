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
/// Issue 10's identity and generated-column cells as the rendered designer shows them: loaded settings
/// fill the Identity, Seed, Increment, Generated, Storage and Generator cells, editing them through the
/// grid's own editors refreshes the preview and the listed problems, and engines without a setting
/// show its editor disabled or read-only with the engine's explanation. Semantics against real
/// databases are in <see cref="IdentityGenerationWorkflowTests"/> and the provider conformance tests.
/// </summary>
public sealed class TableDesignerGenerationHeadlessTests
{
    private static readonly DatabaseObjectIdentity Table = new(null, "s", "items", SchemaNodeType.Table);

    private static TableDesign Design(string provider) => new(Table, provider)
    {
        Columns =
        [
            new TableDesignColumn("id", "integer", false) { Identity = new(TableDesignIdentityKind.Always, "1", "1") { Options = "CACHE 20" } },
            new TableDesignColumn("a", "integer", false),
            new TableDesignColumn("total", "integer", true) { Generated = new("a * 2", TableDesignGeneratedStorage.Stored) },
            new TableDesignColumn("ext", "integer", true) { Default = "nextval('s.ext'::regclass)", Sequence = new("s.ext", false) }
        ],
        PrimaryKey = new TableDesignKey("pk_items", ["id"]),
        Indexes = [new TableDesignIndex("pk_items", true, [new("id", null, false)], TableDesignIndexOrigin.PrimaryKey)],
        Unsupported = new Dictionary<TableDesignFeature, string> { [TableDesignFeature.GeneratedExpressionChanges] = "Not supported by PostgreSQL 16" }
    };

    private static TableDesignerViewModel Designer(string provider)
    {
        var mock = new Mock<IDatabaseProvider>();
        mock.SetupGet(p => p.ProviderName).Returns(provider);
        mock.SetupGet(p => p.Capabilities).Returns(new DatabaseProviderCapabilities("\"", "\"", "LIMIT") { TransactionalDdl = true });
        mock.Setup(p => p.GetSupportedDataTypes()).Returns(["integer"]);
        mock.Setup(p => p.GetColumnDefinitionsAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(() =>
            [
                new ColumnDefinition { Name = "id", DataType = "integer", IsNullable = false, IsPrimaryKey = true },
                new ColumnDefinition { Name = "a", DataType = "integer", IsNullable = false },
                new ColumnDefinition { Name = "total", DataType = "integer" },
                new ColumnDefinition { Name = "ext", DataType = "integer", DefaultValue = "nextval('s.ext'::regclass)" }
            ]);
        mock.Setup(p => p.GetIndexesAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync([]);
        mock.Setup(p => p.GetSchemaAsync(It.IsAny<string>())).ReturnsAsync([]);
        mock.As<ITableDesignSource>()
            .Setup(p => p.GetTableDesignAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Design(provider));
        var session = new DatabaseSession(new ConnectionInfo { Name = "t", Provider = provider }, mock.Object);
        return new TableDesignerViewModel("items", session, new SchemaNode { Name = "items", Type = SchemaNodeType.Table }, Table);
    }

    private static async Task<(ThemedHost Host, TableDesignerView View, DataGrid Grid)> ShowAsync(TableDesignerViewModel designer)
    {
        var view = new TableDesignerView { DataContext = designer };
        var host = ThemedHost.Show(view, "Light", height: 800, width: 1600);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!(designer.Design.IsLoaded && !designer.IsLoading) && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();
            await Task.Delay(10);
        }
        Assert.True(designer.Design.IsLoaded && !designer.IsLoading, "The design did not load.");
        Dispatcher.UIThread.RunJobs();
        host.Window.UpdateLayout();
        return (host, view, view.FindControl<DataGrid>("ColumnGrid")!);
    }

    [AvaloniaFact]
    public async Task IdentityAndGenerationCells_ShowTheLoadedSettings_AndEditingThemRefreshesThePlan()
    {
        var designer = Designer("PostgreSQL");
        var (host, view, grid) = await ShowAsync(designer);
        using var _ = host;
        Assert.Equal(["Always", "1", "1", "", "", "CACHE 20"], Cells(grid, "id", "Identity", "Seed", "Increment", "Generated", "Storage", "Generator"));
        Assert.Equal(["", "", "", "a * 2", "Stored", ""], Cells(grid, "total", "Identity", "Seed", "Increment", "Generated", "Storage", "Generator"));
        Assert.Equal("uses sequence s.ext", Cells(grid, "ext", "Generator").Single());
        Assert.Contains("ALWAYS or BY DEFAULT", view.FindControl<TextBlock>("GenerationNote")!.Text);
        Assert.Equal("", designer.PreviewSql);

        Choose(grid, "id", "Identity", "By default");
        EditText(grid, "id", "Increment", "5");
        EditText(grid, "total", "Generated", "");
        Assert.Equal("""
            ALTER TABLE "s"."items" ALTER COLUMN "id" SET GENERATED BY DEFAULT SET INCREMENT BY 5;
            ALTER TABLE "s"."items" ALTER COLUMN "total" DROP EXPRESSION;
            """.ReplaceLineEndings().TrimEnd(), view.FindControl<TextBox>("DdlPreview")!.Text);
        Assert.True(view.FindControl<Button>("SaveButton")!.IsEnabled);

        // A change the engine version cannot make is listed and disables Save; editing it back clears it.
        EditText(grid, "total", "Generated", "a * 3");
        Assert.Contains("Column total: changing a generated column's expression is Not supported by PostgreSQL 16.", designer.ValidationErrors);
        Assert.False(view.FindControl<Button>("SaveButton")!.IsEnabled);
        EditText(grid, "total", "Generated", "a * 2");
        Assert.Empty(designer.ValidationErrors);

        // A seed is checked against the identity's type, not just for digits.
        EditText(grid, "id", "Seed", "99999999999999999999");
        Assert.Contains("Column \"id\": the identity seed is outside the range of integer.", designer.ValidationErrors);
        EditText(grid, "id", "Seed", "1");

        // Choosing no identity clears the seed and increment cells with it.
        Choose(grid, "id", "Identity", "");
        Assert.Equal(["", "", ""], Cells(grid, "id", "Identity", "Seed", "Increment"));
        Assert.Contains("DROP IDENTITY;", designer.PreviewSql);
    }

    [AvaloniaTheory]
    [InlineData("DuckDB", false, false, "DuckDB has no identity columns")]
    [InlineData("MySQL", true, false, "AUTO_INCREMENT on a key column")]
    public async Task SettingsTheEngineLacks_HaveDisabledOrReadOnlyEditorsAndAnExplanation(string provider, bool identity, bool seed, string note)
    {
        var designer = Designer(provider);
        var (host, view, grid) = await ShowAsync(designer);
        using var _ = host;
        Assert.Contains(note, view.FindControl<TextBlock>("GenerationNote")!.Text);

        var identityEditor = BeginEdit<ComboBox>(grid, "a", "Identity");
        Assert.Equal(identity, identityEditor.IsEnabled);
        Assert.Contains(note, ToolTip.GetTip(identityEditor) as string);
        grid.CancelEdit();
        var seedEditor = BeginEdit<TextBox>(grid, "a", "Seed");
        Assert.Equal(!seed, seedEditor.IsReadOnly);
        grid.CancelEdit();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("", designer.PreviewSql);

        if (provider != "MySQL") return;
        // AUTO_INCREMENT must lead a key: the second column of a composite primary key does not.
        designer.Columns.Add(new ColumnDefinition { Name = "n", DataType = "int", IsNew = true, IsNullable = false, IsPrimaryKey = true, IdentityLabel = "Auto increment" });
        Assert.Contains("Column \"n\": a MySQL Auto increment column must lead the primary key or an index.", designer.ValidationErrors);
    }

    private static DataGridRow Row(DataGrid grid, string column) =>
        grid.GetVisualDescendants().OfType<DataGridRow>().Single(r => (r.DataContext as ColumnDefinition)?.Name == column);

    private static IEnumerable<string> Cells(DataGrid grid, string column, params string[] headers)
    {
        var cells = Row(grid, column).GetVisualDescendants().OfType<DataGridCell>().ToList();
        return headers.Select(h => grid.Columns.Single(c => Equals(c.Header, h)).DisplayIndex)
            .Select(i => cells[i].GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Text ?? "");
    }

    private static T BeginEdit<T>(DataGrid grid, string column, string header) where T : Control
    {
        grid.SelectedItem = Row(grid, column).DataContext;
        grid.CurrentColumn = grid.Columns.Single(c => Equals(c.Header, header));
        Assert.True(grid.BeginEdit());
        Dispatcher.UIThread.RunJobs();
        return Row(grid, column).GetVisualDescendants().OfType<T>().Single();
    }

    private static void EditText(DataGrid grid, string column, string header, string text)
    {
        BeginEdit<TextBox>(grid, column, header).Text = text;
        Assert.True(grid.CommitEdit());
        Dispatcher.UIThread.RunJobs();
    }

    private static void Choose(DataGrid grid, string column, string header, string item)
    {
        BeginEdit<ComboBox>(grid, column, header).SelectedItem = item;
        Assert.True(grid.CommitEdit());
        Dispatcher.UIThread.RunJobs();
    }
}
