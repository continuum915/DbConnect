using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using static DbConnect.Tests.TableDataTestFixtures;

namespace DbConnect.Tests;

public class TableDataTabViewModelTests
{
    [Fact]
    public async Task RefreshInternalAsync_LoadsConfiguredPageAndRestoresSelection()
    {
        var provider = new TablePagingProvider(
            CreatePage([1, 2], hasNextPage: true),
            CreatePage([3]));
        var session = CreateSession(provider);
        var viewModel = new TableDataTabViewModel("Customers", session, CreateTableNode(hasPrimaryKey: true));

        await viewModel.RefreshInternalAsync(bypassDirtyCheck: true);
        viewModel.SelectedRow = viewModel.Rows[1];
        await viewModel.RefreshInternalAsync(bypassDirtyCheck: true);
        await viewModel.NextPageCommand.ExecuteAsync(null);
        await viewModel.PreviousPageCommand.ExecuteAsync(null);

        Assert.Equal(0, viewModel.CurrentPageIndex);
        Assert.Equal(2, viewModel.Rows.Count);
        Assert.Equal(1L, viewModel.SelectedRow!.Values["Id"]);
        Assert.Equal(["Id"], provider.LastOrderByColumns);
    }

    [Fact]
    public async Task CommitAsync_BlocksExistingRowChangesWithoutIdentityOptIn()
    {
        var provider = new TablePagingProvider(CreatePage([1]));
        var session = CreateSession(provider);
        var viewModel = new TableDataTabViewModel("Customers", session, CreateTableNode(hasPrimaryKey: false));

        await viewModel.RefreshInternalAsync(bypassDirtyCheck: true);
        var row = Assert.Single(viewModel.Rows);
        row.Values["Name"] = "Updated";
        viewModel.MarkRowModified(row);

        Assert.Equal(EditState.Unchanged, row.State);
        Assert.Equal("Name 1", row.Values["Name"]);
        Assert.Contains("require", viewModel.StatusMessage);
        Assert.Empty(provider.TransactionCommands);
    }

    [Fact]
    public async Task DuplicateSelectedRow_CreatesInsertedRowAndClearsPrimaryKey()
    {
        var provider = new TablePagingProvider(CreatePage([7]));
        var session = CreateSession(provider);
        var viewModel = new TableDataTabViewModel("Customers", session, CreateTableNode(hasPrimaryKey: true));

        await viewModel.RefreshInternalAsync(bypassDirtyCheck: true);
        viewModel.SelectedRow = viewModel.Rows[0];
        viewModel.DuplicateSelectedRowCommand.Execute(null);

        var duplicate = Assert.Single(viewModel.Rows, row => row.State == EditState.Inserted);
        Assert.Null(duplicate.Values["Id"]);
        Assert.Equal("Name 7", duplicate.Values["Name"]);
    }

    [Fact]
    public async Task DuplicateSelectedRow_MarksCopiedColumnsEditedButNotTheResetPrimaryKey()
    {
        var provider = new TablePagingProvider(CreatePage([7]));
        var session = CreateSession(provider);
        var viewModel = new TableDataTabViewModel("Customers", session, CreateTableNode(hasPrimaryKey: true));

        await viewModel.RefreshInternalAsync(bypassDirtyCheck: true);
        viewModel.SelectedRow = viewModel.Rows[0];
        viewModel.DuplicateSelectedRowCommand.Execute(null);

        var duplicate = Assert.Single(viewModel.Rows, row => row.State == EditState.Inserted);
        Assert.Contains("Name", duplicate.EditedColumns);
        Assert.DoesNotContain("Id", duplicate.EditedColumns);
    }

    [Fact]
    public async Task AddRow_ThenEditingACell_OnlyMarksThatColumnEdited()
    {
        var provider = new TablePagingProvider(CreatePage([1]));
        var session = CreateSession(provider);
        var viewModel = new TableDataTabViewModel("Customers", session, CreateTableNode(hasPrimaryKey: true));

        await viewModel.RefreshInternalAsync(bypassDirtyCheck: true);
        viewModel.AddRowCommand.Execute(null);
        var row = Assert.Single(viewModel.Rows, r => r.State == EditState.Inserted);

        row.Values["Name"] = "New Customer";
        viewModel.MarkRowModified(row, "Name");

        Assert.Equal(["Name"], row.EditedColumns);
    }

    [Fact]
    public async Task RevertCell_RestoresOriginalValueAndLeavesOtherEditsPending()
    {
        var provider = new TablePagingProvider(CreatePage([1]));
        var session = CreateSession(provider);
        var viewModel = new TableDataTabViewModel("Customers", session, CreateTableNode(hasPrimaryKey: true));

        await viewModel.RefreshInternalAsync(bypassDirtyCheck: true);
        var row = Assert.Single(viewModel.Rows);
        row.Values["Id"] = 99L;
        viewModel.MarkRowModified(row, "Id");
        row.Values["Name"] = "Updated";
        viewModel.MarkRowModified(row, "Name");

        Assert.True(viewModel.CanRevertCell(row, "Name"));
        viewModel.RevertCell(row, "Name");

        Assert.Equal("Name 1", row.Values["Name"]);
        Assert.False(viewModel.CanRevertCell(row, "Name"));
        Assert.Equal(99L, row.Values["Id"]);
        Assert.Equal(EditState.Modified, row.State);
        Assert.True(viewModel.HasDirtyRows);
    }

    [Fact]
    public async Task RevertCell_OfTheOnlyEditedCell_ReturnsRowToUnchanged()
    {
        var provider = new TablePagingProvider(CreatePage([1]));
        var session = CreateSession(provider);
        var viewModel = new TableDataTabViewModel("Customers", session, CreateTableNode(hasPrimaryKey: true));

        await viewModel.RefreshInternalAsync(bypassDirtyCheck: true);
        var row = Assert.Single(viewModel.Rows);
        row.Values["Name"] = "Updated";
        viewModel.MarkRowModified(row, "Name");

        viewModel.RevertCell(row, "Name");

        Assert.Equal("Name 1", row.Values["Name"]);
        Assert.Equal(EditState.Unchanged, row.State);
        Assert.False(viewModel.HasDirtyRows);
        Assert.False(viewModel.CanRevert);
    }

    [Fact]
    public async Task RevertCell_OnInsertedRow_RestoresInitialValueAndUnmarksColumn()
    {
        var provider = new TablePagingProvider(CreatePage([1]));
        var session = CreateSession(provider);
        var viewModel = new TableDataTabViewModel("Customers", session, CreateTableNode(hasPrimaryKey: true));

        await viewModel.RefreshInternalAsync(bypassDirtyCheck: true);
        viewModel.AddRowCommand.Execute(null);
        var row = Assert.Single(viewModel.Rows, r => r.State == EditState.Inserted);
        row.Values["Name"] = "New Customer";
        viewModel.MarkRowModified(row, "Name");

        viewModel.RevertCell(row, "Name");

        Assert.Null(row.Values["Name"]);
        Assert.Empty(row.EditedColumns);
        Assert.Equal(EditState.Inserted, row.State);
        Assert.Contains(row, viewModel.Rows);
    }

    [Fact]
    public async Task RevertCell_OnDuplicatedRow_KeepsTheCopiedValueInTheInsert()
    {
        var provider = new TablePagingProvider(CreatePage([7]));
        var session = CreateSession(provider);
        var viewModel = new TableDataTabViewModel("Customers", session, CreateTableNode(hasPrimaryKey: true));

        await viewModel.RefreshInternalAsync(bypassDirtyCheck: true);
        viewModel.SelectedRow = viewModel.Rows[0];
        viewModel.DuplicateSelectedRowCommand.Execute(null);
        var duplicate = Assert.Single(viewModel.Rows, r => r.State == EditState.Inserted);
        duplicate.Values["Name"] = "Changed";
        viewModel.MarkRowModified(duplicate, "Name");

        viewModel.RevertCell(duplicate, "Name");

        Assert.Equal("Name 7", duplicate.Values["Name"]);
        Assert.Contains("Name", duplicate.EditedColumns);
    }

    [Fact]
    public async Task RevertRow_DiscardsAllPendingEditsToAModifiedRowOnly()
    {
        var provider = new TablePagingProvider(CreatePage([1, 2]));
        var session = CreateSession(provider);
        var viewModel = new TableDataTabViewModel("Customers", session, CreateTableNode(hasPrimaryKey: true));

        await viewModel.RefreshInternalAsync(bypassDirtyCheck: true);
        var first = viewModel.Rows[0];
        var second = viewModel.Rows[1];
        first.Values["Id"] = 99L;
        viewModel.MarkRowModified(first, "Id");
        first.Values["Name"] = "Updated";
        viewModel.MarkRowModified(first, "Name");
        second.Values["Name"] = "Other";
        viewModel.MarkRowModified(second, "Name");

        viewModel.RevertRowCommand.Execute(first);

        Assert.Equal(1L, first.Values["Id"]);
        Assert.Equal("Name 1", first.Values["Name"]);
        Assert.Equal(EditState.Unchanged, first.State);
        Assert.Equal(EditState.Modified, second.State);
        Assert.Equal("Other", second.Values["Name"]);
        Assert.Equal(1, viewModel.DirtyRowCount);
    }

    [Fact]
    public async Task RevertCell_ThenCommit_UpdatesOnlyTheStillEditedColumn()
    {
        var provider = new TablePagingProvider(CreatePage([1]));
        var session = CreateSession(provider);
        var viewModel = new TableDataTabViewModel("Customers", session, CreateTableNode(hasPrimaryKey: true));

        await viewModel.RefreshInternalAsync(bypassDirtyCheck: true);
        var row = Assert.Single(viewModel.Rows);
        row.Values["Id"] = 99L;
        viewModel.MarkRowModified(row, "Id");
        row.Values["Name"] = "Updated";
        viewModel.MarkRowModified(row, "Name");

        viewModel.RevertCell(row, "Name");
        await viewModel.ExecuteCommitAsync();

        var update = Assert.Single(provider.TransactionCommands);
        var setClause = update.CommandText[..update.CommandText.IndexOf("WHERE", StringComparison.Ordinal)];
        Assert.Contains("\"Id\"", setClause);
        Assert.DoesNotContain("\"Name\"", setClause);
    }

    [Fact]
    public async Task RevertCell_IsUnavailableOnADeletedRow()
    {
        var provider = new TablePagingProvider(CreatePage([1]));
        var session = CreateSession(provider);
        var viewModel = new TableDataTabViewModel("Customers", session, CreateTableNode(hasPrimaryKey: true));

        await viewModel.RefreshInternalAsync(bypassDirtyCheck: true);
        var row = Assert.Single(viewModel.Rows);
        row.Values["Name"] = "Updated";
        viewModel.MarkRowModified(row, "Name");
        viewModel.DeleteRowCommand.Execute(row);

        viewModel.RevertCell(row, "Name");

        Assert.False(viewModel.CanRevertCell(row, "Name"));
        Assert.Equal("Updated", row.Values["Name"]);
        Assert.Equal(EditState.Deleted, row.State);
    }

    [Fact]
    public async Task RevertRow_OnAnEditedThenDeletedRow_UndeletesItAndDiscardsTheEdits()
    {
        var provider = new TablePagingProvider(CreatePage([1]));
        var session = CreateSession(provider);
        var viewModel = new TableDataTabViewModel("Customers", session, CreateTableNode(hasPrimaryKey: true));

        await viewModel.RefreshInternalAsync(bypassDirtyCheck: true);
        var row = Assert.Single(viewModel.Rows);
        row.Values["Name"] = "Updated";
        viewModel.MarkRowModified(row, "Name");
        viewModel.DeleteRowCommand.Execute(row);

        viewModel.RevertRowCommand.Execute(row);

        Assert.Equal(EditState.Unchanged, row.State);
        Assert.Equal("Name 1", row.Values["Name"]);
        Assert.False(viewModel.HasDirtyRows);
    }

    [Fact]
    public async Task RevertRow_UndeletesADeletedRow()
    {
        var provider = new TablePagingProvider(CreatePage([1]));
        var session = CreateSession(provider);
        var viewModel = new TableDataTabViewModel("Customers", session, CreateTableNode(hasPrimaryKey: true));

        await viewModel.RefreshInternalAsync(bypassDirtyCheck: true);
        var row = Assert.Single(viewModel.Rows);
        viewModel.DeleteRowCommand.Execute(row);

        viewModel.RevertRowCommand.Execute(row);

        Assert.Equal(EditState.Unchanged, row.State);
        Assert.False(viewModel.HasDirtyRows);
    }

    [Fact]
    public async Task RevertRow_OnInsertedRow_RemovesIt()
    {
        var provider = new TablePagingProvider(CreatePage([1]));
        var session = CreateSession(provider);
        var viewModel = new TableDataTabViewModel("Customers", session, CreateTableNode(hasPrimaryKey: true));

        await viewModel.RefreshInternalAsync(bypassDirtyCheck: true);
        viewModel.AddRowCommand.Execute(null);
        var row = Assert.Single(viewModel.Rows, r => r.State == EditState.Inserted);

        viewModel.RevertRowCommand.Execute(row);

        Assert.DoesNotContain(row, viewModel.Rows);
        Assert.Single(viewModel.Rows);
        Assert.False(viewModel.HasDirtyRows);
    }

    [Fact]
    public async Task CopyAndPasteRows_UseTabSeparatedHeaderFormat()
    {
        var provider = new TablePagingProvider(CreatePage([1, 2]));
        var session = CreateSession(provider);
        var viewModel = new TableDataTabViewModel("Customers", session, CreateTableNode(hasPrimaryKey: true));

        await viewModel.RefreshInternalAsync(bypassDirtyCheck: true);

        var copied = viewModel.BuildCopyRowsText([viewModel.Rows[0], viewModel.Rows[1]]);
        var inserted = viewModel.PasteRowsFromText(copied!);

        Assert.Equal(2, inserted);
        Assert.Equal(4, viewModel.Rows.Count);
        Assert.Equal(2, viewModel.Rows.Count(row => row.State == EditState.Inserted));
        Assert.Equal("Id\tName", copied!.Split(Environment.NewLine)[0]);
        Assert.All(viewModel.Rows.Where(row => row.State == EditState.Inserted), row => Assert.Null(row.Values["Id"]));
        Assert.All(viewModel.Rows.Where(row => row.State == EditState.Inserted), row =>
        {
            Assert.Contains("Name", row.EditedColumns);
            Assert.DoesNotContain("Id", row.EditedColumns);
        });
    }

    [Fact]
    public async Task AddRow_CommitsOnlyTheColumnsTheUserSet()
    {
        var provider = new TablePagingProvider(CreatePage([1]));
        var viewModel = new TableDataTabViewModel("Customers", CreateSession(provider), CreateTableNode(hasPrimaryKey: true));
        await viewModel.RefreshInternalAsync(bypassDirtyCheck: true);
        CommitPreviewRequest? preview = null;
        viewModel.CommitPreviewRequested += request => preview = request;

        viewModel.AddRowCommand.Execute(null);
        var row = viewModel.Rows[^1];
        viewModel.ToggleCellNull(row, "Name"); // un-NULL the cell, as the editor's NULL checkbox does
        row.Values["Name"] = "Ada";             // then type into it
        viewModel.MarkRowModified(row, "Name");
        await viewModel.CommitCommand.ExecuteAsync(null);
        await viewModel.ExecuteCommitAsync();

        Assert.Equal("""INSERT INTO "Customers" ("Name") VALUES ('Ada');""", preview!.SqlPreview);
        var command = Assert.Single(provider.TransactionCommands);
        Assert.Equal("""INSERT INTO "Customers" ("Name") VALUES (@p0);""", command.CommandText);
    }

    [Fact]
    public async Task AddRow_WithNoEdits_CommitsAllDefaults()
    {
        var provider = new TablePagingProvider(CreatePage([1]));
        var viewModel = new TableDataTabViewModel("Customers", CreateSession(provider), CreateTableNode(hasPrimaryKey: true));
        await viewModel.RefreshInternalAsync(bypassDirtyCheck: true);
        CommitPreviewRequest? preview = null;
        viewModel.CommitPreviewRequested += request => preview = request;

        viewModel.AddRowCommand.Execute(null);
        await viewModel.CommitCommand.ExecuteAsync(null);

        Assert.Equal("""INSERT INTO "Customers" DEFAULT VALUES;""", preview!.SqlPreview);
    }

    [Fact]
    public async Task DuplicatedAndPastedRows_SendTheirCopiedValuesButNotThePrimaryKey()
    {
        var provider = new TablePagingProvider(CreatePage([7]));
        var viewModel = new TableDataTabViewModel("Customers", CreateSession(provider), CreateTableNode(hasPrimaryKey: true));
        await viewModel.RefreshInternalAsync(bypassDirtyCheck: true);
        CommitPreviewRequest? preview = null;
        viewModel.CommitPreviewRequested += request => preview = request;

        viewModel.SelectedRow = viewModel.Rows[0];
        viewModel.DuplicateSelectedRowCommand.Execute(null);
        viewModel.PasteRowsFromText("Id\tName\n99\tPasted");
        await viewModel.CommitCommand.ExecuteAsync(null);

        Assert.Equal(
            """
            INSERT INTO "Customers" ("Name") VALUES ('Name 7');

            INSERT INTO "Customers" ("Name") VALUES ('Pasted');
            """.ReplaceLineEndings(),
            preview!.SqlPreview.ReplaceLineEndings());
    }

    [Fact]
    public async Task GetEditorKind_ReturnsTheEditorForEachColumnsClassifiedType()
    {
        var provider = new TablePagingProvider(CreateTypedPage());
        var session = CreateSession(provider);
        var viewModel = new TableDataTabViewModel("Widgets", session, CreateTableNode(hasPrimaryKey: true));

        await viewModel.RefreshInternalAsync(bypassDirtyCheck: true);

        Assert.Equal(CellEditorKind.Numeric, viewModel.GetEditorKind("Id"));
        Assert.Equal(CellEditorKind.Text, viewModel.GetEditorKind("Name"));
        Assert.Equal(CellEditorKind.Boolean, viewModel.GetEditorKind("IsActive"));
        Assert.Equal(CellEditorKind.Date, viewModel.GetEditorKind("BirthDate"));
        Assert.Equal(CellEditorKind.DateTime, viewModel.GetEditorKind("CreatedAt"));
        Assert.Equal(CellEditorKind.Numeric, viewModel.GetEditorKind("Price"));
    }

    [Fact]
    public async Task GetEditorKind_EnumColumnWithProviderValues_ReturnsEnumEditor()
    {
        var provider = new TablePagingProvider(
            CreateEnumPage(),
            enumColumns: new Dictionary<string, EnumColumnInfo>
            {
                ["Status"] = new EnumColumnInfo(["active", "retired"], AllowsMultipleValues: false)
            });
        var session = CreateSession(provider);
        var viewModel = new TableDataTabViewModel("Widgets", session, CreateTableNode(hasPrimaryKey: true));

        await viewModel.RefreshInternalAsync(bypassDirtyCheck: true);

        Assert.Equal(CellEditorKind.Enum, viewModel.GetEditorKind("Status"));
        Assert.Equal(["active", "retired"], viewModel.GetEnumValues("Status"));
    }

    [Fact]
    public async Task GetEditorKind_SetColumnWithProviderValues_ReturnsSetEditor()
    {
        var provider = new TablePagingProvider(
            CreateEnumPage(),
            enumColumns: new Dictionary<string, EnumColumnInfo>
            {
                ["Status"] = new EnumColumnInfo(["read", "write"], AllowsMultipleValues: true)
            });
        var session = CreateSession(provider);
        var viewModel = new TableDataTabViewModel("Widgets", session, CreateTableNode(hasPrimaryKey: true));

        await viewModel.RefreshInternalAsync(bypassDirtyCheck: true);

        Assert.Equal(CellEditorKind.Set, viewModel.GetEditorKind("Status"));
    }

    [Fact]
    public async Task GetEditorKind_EnumColumnWithNoProviderValues_FallsBackToTextEditor()
    {
        // A column the resolver already classified as Enum (e.g. MySQL ProviderTypeName "ENUM")
        // but the provider metadata call found nothing for — a dropdown with no options would be
        // useless, so the plain text editor is the safer fallback.
        var provider = new TablePagingProvider(CreateEnumPage());
        var session = CreateSession(provider);
        var viewModel = new TableDataTabViewModel("Widgets", session, CreateTableNode(hasPrimaryKey: true));

        await viewModel.RefreshInternalAsync(bypassDirtyCheck: true);

        Assert.Equal(CellEditorKind.Text, viewModel.GetEditorKind("Status"));
        Assert.Empty(viewModel.GetEnumValues("Status"));
    }

    [Fact]
    public async Task GetEditorKind_TextClassifiedColumnWithProviderEnumValues_IsReclassifiedAsEnum()
    {
        // PostgreSQL native enum columns report their own type name (not "ENUM"), so
        // ColumnTypeResolver classifies them as Text; the provider's own enum metadata catalog
        // is what actually confirms the column is an enum.
        var provider = new TablePagingProvider(
            CreatePgNativeEnumPage(),
            enumColumns: new Dictionary<string, EnumColumnInfo>
            {
                ["Mood"] = new EnumColumnInfo(["happy", "sad"], AllowsMultipleValues: false)
            });
        var session = CreateSession(provider);
        var viewModel = new TableDataTabViewModel("Widgets", session, CreateTableNode(hasPrimaryKey: true));

        await viewModel.RefreshInternalAsync(bypassDirtyCheck: true);

        Assert.Equal(ColumnDataType.Enum, viewModel.GetColumnType("Mood"));
        Assert.Equal(CellEditorKind.Enum, viewModel.GetEditorKind("Mood"));
    }

    [Fact]
    public async Task ToggleCellNull_OnEnumColumn_UsesFirstProviderValueRatherThanEmptyString()
    {
        // CellValueConverter.DefaultNonNullValue's generic "" fallback is not a member of the
        // column's ENUM definition — MySQL strict mode rejects it outright — so the toggle must
        // reach for a value the provider actually reported as valid.
        var provider = new TablePagingProvider(
            CreateEnumPage(),
            enumColumns: new Dictionary<string, EnumColumnInfo>
            {
                ["Status"] = new EnumColumnInfo(["active", "retired"], AllowsMultipleValues: false)
            });
        var session = CreateSession(provider);
        var viewModel = new TableDataTabViewModel("Widgets", session, CreateTableNode(hasPrimaryKey: true));
        await viewModel.RefreshInternalAsync(bypassDirtyCheck: true);
        var row = viewModel.Rows[0];
        viewModel.ToggleCellNull(row, "Status"); // Status -> null

        viewModel.ToggleCellNull(row, "Status");

        Assert.Equal("active", row.Values["Status"]);
    }

    private static TablePageResult CreateEnumPage() =>
        new()
        {
            Result = new QueryResult
            {
                ColumnInfos = [new ColumnInfo { Name = "Status", Ordinal = 0, ProviderTypeName = "ENUM" }],
                ObjectRows = [new object?[] { "active" }]
            }
        };

    private static TablePageResult CreatePgNativeEnumPage() =>
        new()
        {
            Result = new QueryResult
            {
                ColumnInfos = [new ColumnInfo { Name = "Mood", Ordinal = 0, ClrType = typeof(string), ProviderTypeName = "mood" }],
                ObjectRows = [new object?[] { "happy" }]
            }
        };

    [Fact]
    public async Task ToggleCellNull_OnNonNullValue_SetsNullAndMarksRowModified()
    {
        var provider = new TablePagingProvider(CreateTypedPage());
        var session = CreateSession(provider);
        var viewModel = new TableDataTabViewModel("Widgets", session, CreateTableNode(hasPrimaryKey: true));
        await viewModel.RefreshInternalAsync(bypassDirtyCheck: true);
        var row = viewModel.Rows[0];

        viewModel.ToggleCellNull(row, "Price");

        Assert.Null(row.Values["Price"]);
        Assert.Equal(EditState.Modified, row.State);
    }

    [Fact]
    public async Task ToggleCellNull_OnNullValue_SetsATypeCorrectPlaceholderRatherThanEmptyString()
    {
        var provider = new TablePagingProvider(CreateTypedPage());
        var session = CreateSession(provider);
        var viewModel = new TableDataTabViewModel("Widgets", session, CreateTableNode(hasPrimaryKey: true));
        await viewModel.RefreshInternalAsync(bypassDirtyCheck: true);
        var row = viewModel.Rows[0];
        viewModel.ToggleCellNull(row, "Price"); // Price -> null
        row.State = EditState.Unchanged; // isolate the second toggle's own dirty-marking

        viewModel.ToggleCellNull(row, "Price");

        Assert.Equal(0m, row.Values["Price"]);
        Assert.Equal(EditState.Modified, row.State);
    }

    [Fact]
    public async Task ToggleCellNull_TwiceInARow_LandsOnTypeCorrectPlaceholderNotTheOriginalValue()
    {
        var provider = new TablePagingProvider(CreateTypedPage());
        var session = CreateSession(provider);
        var viewModel = new TableDataTabViewModel("Widgets", session, CreateTableNode(hasPrimaryKey: true));
        await viewModel.RefreshInternalAsync(bypassDirtyCheck: true);
        var row = viewModel.Rows[0];

        viewModel.ToggleCellNull(row, "IsActive");
        viewModel.ToggleCellNull(row, "IsActive");

        Assert.Equal(false, row.Values["IsActive"]);
    }
}
