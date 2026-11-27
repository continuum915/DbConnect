using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>
/// RAIN-P2-12 — A commit that finds a row changed underneath the user offers to reload the row or
/// overwrite. Each test edits a row in the tab, then changes the same row through a separate
/// connection before committing, as another user would.
/// </summary>
public sealed class TableDataConcurrencyTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rain-conflict-{Guid.NewGuid():N}.sqlite");
    private readonly IDatabaseProvider _provider = new DatabaseService().GetProvider("SQLite");
    private TableDataTabViewModel _tab = null!;
    private readonly List<ConcurrencyConflictRequest> _conflicts = [];

    private string ConnectionString => $"Data Source={_path};Pooling=False";

    public async Task InitializeAsync()
    {
        await _provider.ExecuteNonQueryAsync(ConnectionString,
            "CREATE TABLE Customers (Id INTEGER PRIMARY KEY, Name TEXT, Email TEXT);" +
            "INSERT INTO Customers VALUES (1, 'Ada', 'ada@example.test'), (2, 'Grace', 'grace@example.test');");

        var table = new SchemaNode { Name = "Customers", RawName = "Customers", Type = SchemaNodeType.Table };
        table.Children.Add(new SchemaNode { Name = "Id", RawName = "Id", Type = SchemaNodeType.Column, IsPrimaryKey = true });
        table.Children.Add(new SchemaNode { Name = "Name", RawName = "Name", Type = SchemaNodeType.Column });
        table.Children.Add(new SchemaNode { Name = "Email", RawName = "Email", Type = SchemaNodeType.Column });
        _tab = new TableDataTabViewModel("Customers", _provider, ConnectionString, table);
        _tab.ConcurrencyConflictRequested += _conflicts.Add;
        await _tab.RefreshInternalAsync(bypassDirtyCheck: true);
    }

    public async Task DisposeAsync()
    {
        await _tab.ReleaseDbSessionAsync();
        try { File.Delete(_path); } catch (IOException) { }
    }

    [Fact]
    public async Task Commit_OfACellChangedUnderneath_RaisesAConflictForThatRow_AndKeepsTheEdits()
    {
        Edit(RowWithId(1), "Name", "Ada (mine)");
        Edit(RowWithId(2), "Name", "Grace (mine)");
        await ChangeConcurrentlyAsync("UPDATE Customers SET Name = 'Grace (theirs)' WHERE Id = 2;");

        await _tab.ExecuteCommitAsync();

        var conflict = Assert.Single(_conflicts);
        Assert.Same(RowWithId(2), conflict.Row);
        Assert.True(conflict.CanOverwrite);
        Assert.Equal(EditState.Modified, RowWithId(1).State);
        Assert.Equal("Grace (mine)", RowWithId(2).Values["Name"]);
        Assert.Equal("Ada", await ReadNameAsync(1)); // the whole commit rolled back
        Assert.Equal("Grace (theirs)", await ReadNameAsync(2));
        Assert.Contains("conflict", _tab.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Commit_OfARowChangedOnlyInOtherColumns_MergesWithoutAConflict()
    {
        Edit(RowWithId(2), "Name", "Grace (mine)");
        await ChangeConcurrentlyAsync("UPDATE Customers SET Email = 'theirs@example.test' WHERE Id = 2;");

        await _tab.ExecuteCommitAsync();

        Assert.Empty(_conflicts);
        Assert.False(_tab.HasDirtyRows);
        Assert.Equal("Grace (mine)", RowWithId(2).Values["Name"]);
        Assert.Equal("theirs@example.test", RowWithId(2).Values["Email"]);
    }

    [Fact]
    public async Task ReloadRow_RefreshesTheRowAndDropsItsPendingEdit_KeepingOtherRowsEdits()
    {
        Edit(RowWithId(1), "Name", "Ada (mine)");
        Edit(RowWithId(2), "Name", "Grace (mine)");
        await ChangeConcurrentlyAsync("UPDATE Customers SET Name = 'Grace (theirs)', Email = 'theirs@example.test' WHERE Id = 2;");
        await _tab.ExecuteCommitAsync();
        var conflicted = Assert.Single(_conflicts).Row;

        await _tab.ReloadConflictedRowAsync(conflicted);

        var reloaded = RowWithId(2);
        Assert.Equal(1, _tab.Rows.IndexOf(reloaded));
        Assert.Equal(EditState.Unchanged, reloaded.State);
        Assert.Equal("Grace (theirs)", reloaded.Values["Name"]);
        Assert.Equal("theirs@example.test", reloaded.Values["Email"]);
        Assert.Equal("Ada (mine)", RowWithId(1).Values["Name"]);
        Assert.Equal(EditState.Modified, RowWithId(1).State);

        // The reloaded row is a fresh baseline: editing it again commits cleanly.
        Edit(reloaded, "Name", "Grace (again)");
        await _tab.ExecuteCommitAsync();
        Assert.Single(_conflicts);
        Assert.Equal("Grace (again)", await ReadNameAsync(2));
        Assert.Equal("Ada (mine)", await ReadNameAsync(1));
    }

    [Fact]
    public async Task Overwrite_CommitsTheUsersEditOverTheConcurrentChange()
    {
        Edit(RowWithId(1), "Name", "Ada (mine)");
        Edit(RowWithId(2), "Name", "Grace (mine)");
        await ChangeConcurrentlyAsync("UPDATE Customers SET Name = 'Grace (theirs)' WHERE Id = 2;");
        await _tab.ExecuteCommitAsync();

        await _tab.OverwriteConflictedRowAsync(Assert.Single(_conflicts).Row);

        Assert.Single(_conflicts);
        Assert.False(_tab.HasDirtyRows);
        Assert.Equal("Ada (mine)", await ReadNameAsync(1));
        Assert.Equal("Grace (mine)", await ReadNameAsync(2));
    }

    [Fact]
    public async Task ARowDeletedUnderneath_CannotBeOverwritten_AndReloadingRemovesIt()
    {
        Edit(RowWithId(2), "Name", "Grace (mine)");
        await ChangeConcurrentlyAsync("DELETE FROM Customers WHERE Id = 2;");
        await _tab.ExecuteCommitAsync();
        var conflict = Assert.Single(_conflicts);

        Assert.False(conflict.CanOverwrite);
        Assert.Contains("deleted", conflict.Message, StringComparison.OrdinalIgnoreCase);

        await _tab.ReloadConflictedRowAsync(conflict.Row);

        Assert.DoesNotContain(conflict.Row, _tab.Rows);
        Assert.Single(_tab.Rows);
        Assert.False(_tab.HasDirtyRows);
    }

    [Fact]
    public async Task Overwrite_OfARowDeletedMeanwhile_RaisesAConflictThatCannotBeOverwrittenAgain()
    {
        Edit(RowWithId(2), "Name", "Grace (mine)");
        await ChangeConcurrentlyAsync("UPDATE Customers SET Name = 'Grace (theirs)' WHERE Id = 2;");
        await _tab.ExecuteCommitAsync();
        await ChangeConcurrentlyAsync("DELETE FROM Customers WHERE Id = 2;");

        await _tab.OverwriteConflictedRowAsync(_conflicts[0].Row);

        Assert.Equal(2, _conflicts.Count);
        Assert.False(_conflicts[1].CanOverwrite);
    }

    [Fact]
    public async Task ADeletedRowChangedUnderneath_StillDeletes()
    {
        RowWithId(2).State = EditState.Deleted;
        await ChangeConcurrentlyAsync("UPDATE Customers SET Name = 'Grace (theirs)' WHERE Id = 2;");

        await _tab.ExecuteCommitAsync();

        Assert.Empty(_conflicts);
        Assert.Single(_tab.Rows);
    }

    private EditableRow RowWithId(long id) => _tab.Rows.Single(r => Equals(r.Values["Id"], id));

    private void Edit(EditableRow row, string column, object? value)
    {
        row.Values[column] = value;
        _tab.MarkRowModified(row, column);
    }

    private Task ChangeConcurrentlyAsync(string sql) => _provider.ExecuteNonQueryAsync(ConnectionString, sql);

    private async Task<object?> ReadNameAsync(long id)
    {
        var result = await _provider.ExecuteQueryAsync(ConnectionString, $"SELECT Name FROM Customers WHERE Id = {id};");
        return result.ObjectRows.Count == 1 ? result.ObjectRows[0][0] : null;
    }
}
