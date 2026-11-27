using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using Xunit;

namespace DbConnect.Tests;

/// <summary>
/// RAIN-P2-16 — Query results that map to one keyed table are editable and commit through the
/// change tracker, the commit preview and the tab session's transaction; others are read-only with a reason.
/// </summary>
public sealed class QueryResultEditorViewModelTests : IAsyncLifetime
{
    private readonly string _databaseFile = Path.Combine(Path.GetTempPath(), $"rain-result-edit-{Guid.NewGuid():N}.sqlite");
    private DatabaseSession _connection = null!;
    private IDbSession _session = null!;

    public async Task InitializeAsync()
    {
        _connection = new DatabaseService().CreateSession(new ConnectionInfo
        {
            Name = "edit",
            Provider = "SQLite",
            ConnectionString = $"Data Source={_databaseFile}"
        });
        _session = await _connection.Provider.OpenSessionAsync(_connection.Connection);
        await RunAsync("""
            CREATE TABLE customers (id INTEGER PRIMARY KEY, email TEXT NOT NULL);
            CREATE TABLE orders (id INTEGER PRIMARY KEY, customer_id INTEGER, status TEXT, total INTEGER);
            CREATE TABLE log (message TEXT);
            INSERT INTO customers VALUES (1, 'ada@example.com');
            INSERT INTO orders VALUES (1, 1, 'new', 100), (2, 1, 'new', 250), (12, 1, 'new', 5);
            INSERT INTO log VALUES ('hello');
            """);
    }

    public async Task DisposeAsync()
    {
        await _session.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _databaseFile, _databaseFile + "-journal" })
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private async Task RunAsync(string sql)
    {
        var result = await _session.ExecuteQueryAsync(sql);
        Assert.False(result.HasError, result.ErrorMessage);
    }

    private async Task<(QueryResultEditorViewModel Editor, IReadOnlyList<QueryResultDisplayRow> Rows)> LoadAsync(string sql)
    {
        var result = await _session.ExecuteQueryAsync(sql);
        Assert.False(result.HasError, result.ErrorMessage);
        var displayRows = QueryResultFormattingService.BuildDisplayRows(result);
        var editor = new QueryResultEditorViewModel(() => Task.FromResult(_session));
        await editor.LoadAsync(result, displayRows, _connection);
        return (editor, displayRows);
    }

    [Fact]
    public async Task SingleTableResultWithItsKey_IsEditable()
    {
        var (editor, rows) = await LoadAsync("SELECT * FROM orders WHERE id < 10");

        Assert.True(editor.IsEditable);
        Assert.Null(editor.ReadOnlyReason);
        Assert.All(rows, row => Assert.NotNull(editor.RowFor(row.DisplayValues)));
    }

    [Fact]
    public async Task EditedCell_CommitsThroughThePreviewAndTheTabSession()
    {
        var (editor, rows) = await LoadAsync("SELECT * FROM orders WHERE id < 10");
        var row = editor.RowFor(rows[0].DisplayValues)!;
        CommitPreviewRequest? preview = null;
        editor.CommitPreviewRequested += request => preview = request;

        Assert.Null(editor.SetCellText(row, 2, "shipped"));
        Assert.True(editor.HasChanges);
        editor.RequestCommitCommand.Execute(null);

        Assert.NotNull(preview);
        Assert.Contains("""UPDATE "orders" SET "status" = 'shipped' WHERE "id" = 1""", preview!.SqlPreview);

        await editor.ExecuteCommitAsync();

        Assert.False(editor.HasChanges);
        var after = await _session.ExecuteQueryAsync("SELECT status FROM orders ORDER BY id");
        Assert.Equal(["shipped", "new", "new"], after.ObjectRows.Select(r => (string?)r[0]));
    }

    [Fact]
    public async Task CommittedRow_CanBeEditedAgainFromItsNewValues()
    {
        var (editor, rows) = await LoadAsync("SELECT id, status FROM orders WHERE id = 1");
        var row = editor.RowFor(rows[0].DisplayValues)!;
        editor.SetCellText(row, 1, "shipped");
        await editor.ExecuteCommitAsync();

        row = editor.RowFor(rows[0].DisplayValues)!;
        Assert.Equal("shipped", editor.GetValue(row, 1));
        editor.SetCellText(row, 1, "delivered");
        await editor.ExecuteCommitAsync();

        var after = await _session.ExecuteQueryAsync("SELECT status FROM orders WHERE id = 1");
        Assert.Equal("delivered", after.ObjectRows.Single()[0]);
        Assert.False(editor.HasChanges);
    }

    [Fact]
    public async Task AliasedColumn_WritesBackToItsSourceColumn()
    {
        var (editor, rows) = await LoadAsync("SELECT o.id AS order_no, o.status AS state FROM orders o WHERE o.id = 2");
        var row = editor.RowFor(rows[0].DisplayValues)!;

        editor.SetCellText(row, 1, "cancelled");
        await editor.ExecuteCommitAsync();

        var after = await _session.ExecuteQueryAsync("SELECT status FROM orders WHERE id = 2");
        Assert.Equal("cancelled", after.ObjectRows.Single()[0]);
    }

    [Fact]
    public async Task NumericCell_ParsesTheTextForItsType_AndRejectsGarbage()
    {
        var (editor, rows) = await LoadAsync("SELECT id, total FROM orders WHERE id = 1");
        var row = editor.RowFor(rows[0].DisplayValues)!;

        Assert.NotNull(editor.SetCellText(row, 1, "lots"));
        Assert.False(editor.HasChanges);

        Assert.Null(editor.SetCellText(row, 1, "125"));
        await editor.ExecuteCommitAsync();

        var after = await _session.ExecuteQueryAsync("SELECT total FROM orders WHERE id = 1");
        Assert.Equal(125L, after.ObjectRows.Single()[0]);
    }

    [Fact]
    public async Task CellSetToNull_CommitsNull()
    {
        var (editor, rows) = await LoadAsync("SELECT id, status FROM orders WHERE id = 1");

        editor.SetCellNull(editor.RowFor(rows[0].DisplayValues)!, 1);
        await editor.ExecuteCommitAsync();

        var after = await _session.ExecuteQueryAsync("SELECT status FROM orders WHERE id = 1");
        Assert.Null(after.ObjectRows.Single()[0]);
    }

    [Fact]
    public async Task Commit_WritesTheSavedValuesIntoTheResult_SoCopySortAndExportSeeThem()
    {
        var result = await _session.ExecuteQueryAsync("SELECT id, status FROM orders WHERE id = 1");
        var displayRows = QueryResultFormattingService.BuildDisplayRows(result);
        var editor = new QueryResultEditorViewModel(() => Task.FromResult(_session));
        await editor.LoadAsync(result, displayRows, _connection);

        editor.SetCellText(editor.RowFor(displayRows[0].DisplayValues)!, 1, "shipped");
        await editor.ExecuteCommitAsync();

        Assert.Equal("shipped", result.ObjectRows[0][1]);
    }

    [Fact]
    public async Task EditsWhileACommitRuns_AreRefused()
    {
        var gate = new TaskCompletionSource<IDbSession>();
        var result = await _session.ExecuteQueryAsync("SELECT id, status FROM orders WHERE id < 10");
        var displayRows = QueryResultFormattingService.BuildDisplayRows(result);
        var editor = new QueryResultEditorViewModel(() => gate.Task);
        await editor.LoadAsync(result, displayRows, _connection);
        editor.SetCellText(editor.RowFor(displayRows[0].DisplayValues)!, 1, "shipped");

        var commit = editor.ExecuteCommitAsync();
        var refused = editor.SetCellText(editor.RowFor(displayRows[1].DisplayValues)!, 1, "late");
        gate.SetResult(_session);
        await commit;

        Assert.NotNull(refused);
        Assert.Equal("new", editor.GetValue(editor.RowFor(displayRows[1].DisplayValues)!, 1));
    }

    [Fact]
    public async Task CommitFinishingAfterANewResultLoaded_LeavesTheNewResultAlone()
    {
        var gate = new TaskCompletionSource<IDbSession>();
        var first = await _session.ExecuteQueryAsync("SELECT id, status FROM orders WHERE id = 1");
        var firstRows = QueryResultFormattingService.BuildDisplayRows(first);
        var editor = new QueryResultEditorViewModel(() => gate.Task);
        await editor.LoadAsync(first, firstRows, _connection);
        editor.SetCellText(editor.RowFor(firstRows[0].DisplayValues)!, 1, "shipped");

        var commit = editor.ExecuteCommitAsync();
        var second = await _session.ExecuteQueryAsync("SELECT id, status FROM orders WHERE id = 1");
        var secondRows = QueryResultFormattingService.BuildDisplayRows(second);
        await editor.LoadAsync(second, secondRows, _connection);
        gate.SetResult(_session);
        await commit;

        Assert.False(editor.HasChanges);
        Assert.Null(editor.RowFor(firstRows[0].DisplayValues));
        Assert.Equal("new", editor.GetValue(editor.RowFor(secondRows[0].DisplayValues)!, 1));
    }

    [Fact]
    public async Task EmptyEditOfANullCell_LeavesItNull()
    {
        // The text editor shows NULL as empty; committing it untouched must not write ''.
        await RunAsync("UPDATE orders SET status = NULL WHERE id = 1");
        var (editor, rows) = await LoadAsync("SELECT id, status FROM orders WHERE id = 1");
        var row = editor.RowFor(rows[0].DisplayValues)!;

        Assert.Null(editor.SetCellText(row, 1, ""));

        Assert.False(editor.HasChanges);
        Assert.Null(editor.GetValue(row, 1));
    }

    [Fact]
    public async Task ChangedCell_IsReported_UntilItsValueIsPutBack()
    {
        var (editor, rows) = await LoadAsync("SELECT id, status FROM orders WHERE id = 1");
        var row = editor.RowFor(rows[0].DisplayValues)!;

        editor.SetCellText(row, 1, "shipped");
        Assert.True(editor.IsCellChanged(row, 1));
        Assert.False(editor.IsCellChanged(row, 0));

        editor.SetCellText(row, 1, "new");
        Assert.False(editor.IsCellChanged(row, 1));
        Assert.False(editor.HasChanges);
    }

    [Fact]
    public async Task DiscardingChanges_RestoresTheLoadedValues()
    {
        var (editor, rows) = await LoadAsync("SELECT id, status FROM orders WHERE id = 1");
        var row = editor.RowFor(rows[0].DisplayValues)!;
        editor.SetCellText(row, 1, "shipped");

        editor.DiscardChangesCommand.Execute(null);

        Assert.False(editor.HasChanges);
        Assert.Equal("new", editor.GetValue(row, 1));
    }

    [Fact]
    public async Task CommitConflict_KeepsTheEditAndReportsIt()
    {
        var (editor, rows) = await LoadAsync("SELECT id, status FROM orders WHERE id = 1");
        var row = editor.RowFor(rows[0].DisplayValues)!;
        editor.SetCellText(row, 1, "mine");
        await RunAsync("UPDATE orders SET status = 'theirs' WHERE id = 1");

        await editor.ExecuteCommitAsync();

        Assert.True(editor.HasChanges);
        Assert.Contains("conflict", editor.StatusMessage, StringComparison.OrdinalIgnoreCase);
        var after = await _session.ExecuteQueryAsync("SELECT status FROM orders WHERE id = 1");
        Assert.Equal("theirs", after.ObjectRows.Single()[0]);
    }

    private async Task<(QueryResultEditorViewModel Editor, QueryResult Result, EditableRow Row, ConcurrencyConflictRequest? Conflict)> CommitAgainstAChangedRowAsync(string theirChange)
    {
        var result = await _session.ExecuteQueryAsync("SELECT id, status FROM orders WHERE id = 1");
        var displayRows = QueryResultFormattingService.BuildDisplayRows(result);
        var editor = new QueryResultEditorViewModel(() => Task.FromResult(_session));
        await editor.LoadAsync(result, displayRows, _connection);
        var row = editor.RowFor(displayRows[0].DisplayValues)!;
        ConcurrencyConflictRequest? conflict = null;
        editor.ConcurrencyConflictRequested += request => conflict = request;
        editor.SetCellText(row, 1, "mine");
        await RunAsync(theirChange);

        await editor.ExecuteCommitAsync();
        return (editor, result, row, conflict);
    }

    [Fact]
    public async Task ConflictOnAChangedRow_AsksToReloadOrOverwrite()
    {
        var (editor, _, row, conflict) = await CommitAgainstAChangedRowAsync("UPDATE orders SET status = 'theirs' WHERE id = 1");

        Assert.True(conflict != null, editor.StatusMessage);
        Assert.Same(row, conflict!.Row);
        Assert.True(conflict.CanOverwrite);
    }

    [Fact]
    public async Task ConflictOnADeletedRow_OffersOnlyReload()
    {
        var (editor, _, _, conflict) = await CommitAgainstAChangedRowAsync("DELETE FROM orders WHERE id = 1");

        Assert.True(conflict != null, editor.StatusMessage);
        Assert.False(conflict!.CanOverwrite);
    }

    [Fact]
    public async Task OverwritingAConflict_CommitsTheUsersValue()
    {
        var (editor, result, row, _) = await CommitAgainstAChangedRowAsync("UPDATE orders SET status = 'theirs' WHERE id = 1");

        await editor.OverwriteConflictedRowAsync(row);

        var after = await _session.ExecuteQueryAsync("SELECT status FROM orders WHERE id = 1");
        Assert.Equal("mine", after.ObjectRows.Single()[0]);
        Assert.False(editor.HasChanges);
        Assert.Equal("mine", result.ObjectRows[0][1]);
    }

    [Fact]
    public async Task ReloadingAConflict_ShowsTheirValue_AndDropsTheEdit()
    {
        var (editor, result, row, _) = await CommitAgainstAChangedRowAsync("UPDATE orders SET status = 'theirs' WHERE id = 1");

        await editor.ReloadConflictedRowAsync(row);

        Assert.False(editor.HasChanges);
        Assert.Equal("theirs", result.ObjectRows[0][1]);
        var displayRows = QueryResultFormattingService.BuildDisplayRows(result);
        await editor.LoadAsync(result, displayRows, _connection);
        Assert.Equal("theirs", editor.GetValue(editor.RowFor(displayRows[0].DisplayValues)!, 1));
    }

    [Fact]
    public async Task ReloadingADeletedRow_DropsTheEdit_AndSaysTheRowIsGone()
    {
        var (editor, _, row, _) = await CommitAgainstAChangedRowAsync("DELETE FROM orders WHERE id = 1");

        await editor.ReloadConflictedRowAsync(row);

        Assert.False(editor.HasChanges);
        Assert.Contains("no longer exists", editor.StatusMessage);
    }

    [Fact]
    public async Task JoinResult_IsReadOnly_WithAReason()
    {
        var (editor, _) = await LoadAsync("SELECT o.id, o.status, c.email FROM orders o JOIN customers c ON c.id = o.customer_id");

        Assert.False(editor.IsEditable);
        Assert.Contains("orders", editor.ReadOnlyReason);
        Assert.Contains("customers", editor.ReadOnlyReason);
    }

    [Fact]
    public async Task ResultWithoutTheKey_IsReadOnly_WithAReason()
    {
        var (editor, _) = await LoadAsync("SELECT status, total FROM orders");

        Assert.False(editor.IsEditable);
        Assert.Contains("id", editor.ReadOnlyReason);
    }

    [Fact]
    public async Task TableWithoutAKey_IsReadOnly_WithAReason()
    {
        var (editor, _) = await LoadAsync("SELECT * FROM log");

        Assert.False(editor.IsEditable);
        Assert.Contains("primary key", editor.ReadOnlyReason);
    }

    [Fact]
    public async Task ReadOnlyResult_RefusesEdits()
    {
        var (editor, rows) = await LoadAsync("SELECT status FROM orders");

        Assert.Null(editor.RowFor(rows[0].DisplayValues));
        Assert.False(editor.HasChanges);
    }

    [Fact]
    public async Task LoadingANewResult_ReplacesTheOldRows()
    {
        var (editor, oldRows) = await LoadAsync("SELECT * FROM orders WHERE id < 10");
        var result = await _session.ExecuteQueryAsync("SELECT o.id, c.email FROM orders o JOIN customers c ON c.id = o.customer_id");

        await editor.LoadAsync(result, QueryResultFormattingService.BuildDisplayRows(result), _connection);

        Assert.False(editor.IsEditable);
        Assert.Null(editor.RowFor(oldRows[0].DisplayValues));
    }

    [Fact]
    public async Task ReloadingTheSameResult_KeepsPendingEdits()
    {
        // The tab rebuilds its display rows when the user switches result sets or the grid refreshes.
        var result = await _session.ExecuteQueryAsync("SELECT id, status FROM orders WHERE id = 1");
        var editor = new QueryResultEditorViewModel(() => Task.FromResult(_session));
        var first = QueryResultFormattingService.BuildDisplayRows(result);
        await editor.LoadAsync(result, first, _connection);
        editor.SetCellText(editor.RowFor(first[0].DisplayValues)!, 1, "x");
        var rebuilt = QueryResultFormattingService.BuildDisplayRows(result);

        await editor.LoadAsync(result, rebuilt, _connection);

        Assert.True(editor.HasChanges);
        Assert.Equal("x", editor.GetValue(editor.RowFor(rebuilt[0].DisplayValues)!, 1));
    }

    [Fact]
    public async Task ConfirmingADiscard_WithoutPendingEdits_DoesNotAsk()
    {
        var (editor, _) = await LoadAsync("SELECT id, status FROM orders WHERE id = 1");
        var asked = false;
        editor.DiscardConfirmation = _ => { asked = true; return Task.FromResult(true); };

        Assert.True(await editor.ConfirmDiscardingChangesAsync());
        Assert.False(asked);
    }

    [Fact]
    public async Task DecliningADiscard_KeepsThePendingEdits()
    {
        var (editor, rows) = await LoadAsync("SELECT id, status FROM orders WHERE id < 10");
        editor.SetCellText(editor.RowFor(rows[0].DisplayValues)!, 1, "a");
        editor.SetCellText(editor.RowFor(rows[1].DisplayValues)!, 1, "b");
        int? askedAbout = null;
        editor.DiscardConfirmation = count => { askedAbout = count; return Task.FromResult(false); };

        Assert.False(await editor.ConfirmDiscardingChangesAsync());
        Assert.Equal(2, askedAbout);
        Assert.True(editor.HasChanges);
    }

    [Fact]
    public async Task AcceptingADiscard_RevertsThePendingEdits()
    {
        var (editor, rows) = await LoadAsync("SELECT id, status FROM orders WHERE id = 1");
        var row = editor.RowFor(rows[0].DisplayValues)!;
        editor.SetCellText(row, 1, "a");
        editor.DiscardConfirmation = _ => Task.FromResult(true);

        Assert.True(await editor.ConfirmDiscardingChangesAsync());
        Assert.False(editor.HasChanges);
        Assert.Equal("new", editor.GetValue(row, 1));
    }

    [Fact]
    public async Task PendingEdits_WithNoWayToAsk_AreKept()
    {
        var (editor, rows) = await LoadAsync("SELECT id, status FROM orders WHERE id = 1");
        editor.SetCellText(editor.RowFor(rows[0].DisplayValues)!, 1, "a");

        Assert.False(await editor.ConfirmDiscardingChangesAsync());
        Assert.True(editor.HasChanges);
    }

    [Fact]
    public async Task ResultFromAnotherConnection_IsReadOnly()
    {
        // The tab switched connections after the query ran; a commit would go to the new one.
        var (editor, _) = await LoadAsync("SELECT * FROM orders WHERE id < 10");
        var result = await _session.ExecuteQueryAsync("SELECT * FROM orders WHERE id < 10");
        await editor.LoadAsync(result, QueryResultFormattingService.BuildDisplayRows(result), _connection);
        var other = new DatabaseService().CreateSession(new ConnectionInfo
        {
            Name = "other",
            Provider = "SQLite",
            ConnectionString = $"Data Source={_databaseFile}"
        });

        await editor.LoadAsync(result, QueryResultFormattingService.BuildDisplayRows(result), other);

        Assert.False(editor.IsEditable);
        Assert.Contains("connection", editor.ReadOnlyReason);

        // ...and stays read-only however often the tab redraws it.
        await editor.LoadAsync(result, QueryResultFormattingService.BuildDisplayRows(result), other);
        Assert.False(editor.IsEditable);
    }

    [Fact]
    public async Task NoResult_IsNotEditable_AndHasNoReasonToShow()
    {
        var editor = new QueryResultEditorViewModel(() => Task.FromResult(_session));

        await editor.LoadAsync(null, [], _connection);

        Assert.False(editor.IsEditable);
        Assert.Null(editor.ReadOnlyReason);
    }
}
