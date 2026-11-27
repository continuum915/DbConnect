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
/// The designer's shared preview/save plan (issue 07) as the rendered view shows it: property edits
/// refresh the preview, problems disable Save and are listed, Save runs once at a time, and a
/// failed reload after saving is not reported as a clean save. Semantics are covered against real
/// databases in <see cref="TableChangePlanWorkflowTests"/>.
/// </summary>
public sealed class TableDesignerPlanHeadlessTests
{
    private static readonly DatabaseObjectIdentity Table = new(null, "s", "items", SchemaNodeType.Table);

    private static TableDesign Design() => new(Table, "PostgreSQL")
    {
        Columns =
        [
            new TableDesignColumn("id", "integer", false),
            new TableDesignColumn("name", "text", true) { Collation = "\"C\"", Properties = ["STORAGE external"] },
            new TableDesignColumn("note", "text", true) { Comment = "free text" }
        ],
        PrimaryKey = new TableDesignKey("pk_items", ["id"]),
        Indexes = [new TableDesignIndex("pk_items", true, [new("id", null, false)], TableDesignIndexOrigin.PrimaryKey)]
    };

    private static (TableDesignerViewModel Designer, Mock<IDatabaseProvider> Provider) Designer(Func<Task<TableDesign>> load)
    {
        var mock = new Mock<IDatabaseProvider>();
        mock.SetupGet(p => p.ProviderName).Returns("PostgreSQL");
        mock.SetupGet(p => p.Capabilities).Returns(new DatabaseProviderCapabilities("\"", "\"", "LIMIT")
        {
            SupportsObjectComments = true,
            SupportsTableDesignerDropColumn = true,
            TransactionalDdl = true
        });
        mock.Setup(p => p.GetSupportedDataTypes()).Returns(["integer", "text", "varchar(20)"]);
        mock.Setup(p => p.GetColumnDefinitionsAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(() =>
            [
                new ColumnDefinition { Name = "id", DataType = "integer", IsNullable = false, IsPrimaryKey = true },
                new ColumnDefinition { Name = "name", DataType = "text" },
                new ColumnDefinition { Name = "note", DataType = "text" }
            ]);
        mock.Setup(p => p.GetIndexesAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync([]);
        mock.Setup(p => p.GetSchemaAsync(It.IsAny<string>())).ReturnsAsync([]);
        mock.As<ITableDesignSource>()
            .Setup(p => p.GetTableDesignAsync(It.IsAny<string>(), It.IsAny<DatabaseObjectIdentity>(), It.IsAny<CancellationToken>()))
            .Returns(load);
        var session = new DatabaseSession(new ConnectionInfo { Name = "t", Provider = "PostgreSQL" }, mock.Object);
        return (new TableDesignerViewModel("items", session, new SchemaNode { Name = "items", Type = SchemaNodeType.Table }, Table), mock);
    }

    private static ThemedHost Show(TableDesignerViewModel designer, out TableDesignerView view)
    {
        view = new TableDesignerView { DataContext = designer };
        return ThemedHost.Show(view, "Light", height: 800, width: 1200);
    }

    private static async Task Loaded(ThemedHost host, TableDesignerViewModel designer)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!(designer.Design.IsLoaded && !designer.IsLoading) && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();
            await Task.Delay(10);
        }
        Assert.True(designer.Design.IsLoaded && !designer.IsLoading, "The design did not load.");
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task EditingAColumnProperty_RefreshesThePreviewAndEditingItBackClearsIt()
    {
        var (designer, _) = Designer(() => Task.FromResult(Design()));
        using var host = Show(designer, out var view);
        await Loaded(host, designer);
        var preview = view.FindControl<TextBox>("DdlPreview")!;
        var save = view.FindControl<Button>("SaveButton")!;
        Assert.Equal("", preview.Text);
        Assert.Equal("free text", designer.Columns.Single(c => c.Name == "note").Comment);

        var note = designer.Columns.Single(c => c.Name == "note");
        note.DataType = "varchar(20)";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("""ALTER TABLE "s"."items" ALTER COLUMN "note" TYPE varchar(20);""", preview.Text);
        // A type change can alter stored values, so Save waits for it to be confirmed.
        Assert.True(view.FindControl<Border>("DestructiveBanner")!.IsVisible);
        Assert.False(save.IsEnabled);
        view.FindControl<CheckBox>("ConfirmDestructive")!.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(save.IsEnabled);

        note.DataType = "text";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("", preview.Text);
        Assert.False(designer.Review.DestructiveConfirmed);

        designer.TableComment = "Stock items";
        note.Comment = "";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("""
            COMMENT ON TABLE "s"."items" IS 'Stock items';
            COMMENT ON COLUMN "s"."items"."note" IS NULL;
            """.ReplaceLineEndings().TrimEnd(), preview.Text);
    }

    /// <summary>
    /// Issue 09: the loaded declared type is shown as its type name with Length, Precision, Scale
    /// and Collation cells, and editing those cells in the grid refreshes the preview and the
    /// listed problems.
    /// </summary>
    [AvaloniaFact]
    public async Task TypeModifierAndCollationCells_ShowTheLoadedTypeAndEditThePlan()
    {
        var design = Design() with
        {
            Columns =
            [
                new TableDesignColumn("id", "integer", false),
                new TableDesignColumn("name", "text", true) { Collation = "\"C\"" },
                new TableDesignColumn("note", "character varying(80)", true)
            ]
        };
        var (designer, _) = Designer(() => Task.FromResult(design));
        using var host = Show(designer, out var view);
        await Loaded(host, designer);
        var grid = view.FindControl<DataGrid>("ColumnGrid")!;
        host.Window.UpdateLayout();
        Assert.Equal(["note", "character varying", "80", "", "", ""], Cells(grid, "note").Take(6));
        Assert.Equal(["name", "text", "", "", "", "\"C\""], Cells(grid, "name").Take(6));

        Edit(grid, "note", "Length", "120");
        Edit(grid, "name", "Collation", "\"POSIX\"");
        var preview = view.FindControl<TextBox>("DdlPreview")!;
        Assert.Equal("""
            ALTER TABLE "s"."items" ALTER COLUMN "name" TYPE text COLLATE "POSIX";
            ALTER TABLE "s"."items" ALTER COLUMN "note" TYPE character varying(120);
            """.ReplaceLineEndings().TrimEnd(), preview.Text);

        Edit(grid, "id", "Precision", "5");
        Assert.Contains("Column \"id\": PostgreSQL type integer takes no length, precision or scale.", designer.ValidationErrors);
        Assert.False(view.FindControl<Button>("SaveButton")!.IsEnabled);
        Edit(grid, "id", "Precision", "");
        Assert.Empty(designer.ValidationErrors);
    }

    [AvaloniaTheory]
    [InlineData("character varying(4)", false)]
    [InlineData("character(4)", true)]
    [InlineData("bit(4)", true)]
    public async Task LengtheningAPaddedFixedLengthType_StillNeedsReview(string loaded, bool destructive)
    {
        var design = Design() with
        {
            Columns = [new TableDesignColumn("id", "integer", false), new TableDesignColumn("name", "text", true),
                new TableDesignColumn("note", loaded, true)]
        };
        var (designer, _) = Designer(() => Task.FromResult(design));
        using var host = Show(designer, out _);
        await Loaded(host, designer);
        designer.Columns.Single(c => c.Name == "note").Length = "8";
        Assert.True(designer.Plan.HasChanges);
        Assert.Equal(destructive, designer.Plan.Destructive.Count == 1);
    }

    private static DataGridRow Row(DataGrid grid, string column) =>
        grid.GetVisualDescendants().OfType<DataGridRow>().Single(r => (r.DataContext as ColumnDefinition)?.Name == column);

    private static IEnumerable<string> Cells(DataGrid grid, string column) =>
        Row(grid, column).GetVisualDescendants().OfType<DataGridCell>()
            .Select(c => c.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Text ?? "");

    /// <summary>Edits one cell through the grid's own editing template and commits it.</summary>
    private static void Edit(DataGrid grid, string column, string header, string text)
    {
        var row = Row(grid, column);
        grid.SelectedItem = row.DataContext;
        grid.CurrentColumn = grid.Columns.Single(c => Equals(c.Header, header));
        Assert.True(grid.BeginEdit());
        Dispatcher.UIThread.RunJobs();
        var editor = Row(grid, column).GetVisualDescendants().OfType<TextBox>().Single();
        editor.Text = text;
        Assert.True(grid.CommitEdit());
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task AnEditThatWouldLoseALoadedProperty_IsListedAndDisablesSave()
    {
        var (designer, mock) = Designer(() => Task.FromResult(Design()));
        using var host = Show(designer, out var view);
        await Loaded(host, designer);

        designer.Columns.Single(c => c.Name == "name").DataType = "varchar(20)";
        Dispatcher.UIThread.RunJobs();
        Assert.False(view.FindControl<Button>("SaveButton")!.IsEnabled);
        var problem = Assert.Single(designer.ValidationErrors);
        Assert.Contains("Column name: changing its type could reset its STORAGE external", problem);

        // Unchecking the only primary-key column drops the key (issue 11), which needs review.
        designer.Columns.Single(c => c.Name == "id").IsPrimaryKey = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("DROP CONSTRAINT \"pk_items\"", designer.PreviewSql);
        Assert.Contains(designer.Plan.Destructive, d => d.StartsWith("Drops Primary key pk_items"));

        await designer.SaveCommand.ExecuteAsync(null);
        Assert.Equal($"⚠️ {designer.ValidationErrors[0]}", designer.StatusMessage);
        mock.Verify(p => p.ExecuteTransactionAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>()), Times.Never);
    }

    [AvaloniaFact]
    public async Task SaveRunsOnceAtATimeAndRunsThePreviewedSql()
    {
        var (designer, mock) = Designer(() => Task.FromResult(Design()));
        var executing = new TaskCompletionSource<TransactionResult>();
        var executed = new List<string>();
        mock.Setup(p => p.ExecuteTransactionAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>()))
            .Returns((string _, IReadOnlyList<string> steps) => { executed.Add(string.Join(Environment.NewLine, steps)); return executing.Task; });
        using var host = Show(designer, out var view);
        await Loaded(host, designer);
        designer.Columns.Add(new ColumnDefinition { Name = "added", DataType = "integer", IsNew = true });
        var preview = designer.PreviewSql;

        var first = designer.SaveCommand.ExecuteAsync(null);
        var second = designer.SaveCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(view.FindControl<Button>("SaveButton")!.IsEnabled);
        executing.SetResult(TransactionResult.Success(0));
        await Task.WhenAll(first, second);

        Assert.Equal([preview], executed);
        Assert.Equal("""ALTER TABLE "s"."items" ADD "added" integer;""", preview);
    }

    [AvaloniaFact]
    public async Task AFailedReloadAfterSaving_IsNotReportedAsACleanSave()
    {
        var loads = 0;
        // Loads: the designer opening, the re-read just before saving, then the reload after saving.
        var (designer, mock) = Designer(() => ++loads <= 2
            ? Task.FromResult(Design())
            : Task.FromException<TableDesign>(new TableDefinitionUnavailableException("permission denied for pg_index")));
        mock.Setup(p => p.ExecuteTransactionAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>())).ReturnsAsync(TransactionResult.Success(0));
        using var host = Show(designer, out var view);
        await Loaded(host, designer);
        designer.Columns.Add(new ColumnDefinition { Name = "added", DataType = "integer", IsNew = true });

        await designer.SaveCommand.ExecuteAsync(null);

        Assert.Contains("saved, but the table could not be reloaded", designer.StatusMessage);
        Assert.DoesNotContain("successfully", designer.StatusMessage);
        Assert.True(designer.Design.IsFailed);
        Dispatcher.UIThread.RunJobs();
        Assert.False(view.FindControl<Button>("SaveButton")!.IsEnabled);
    }
}
