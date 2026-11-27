using Microsoft.Data.Sqlite;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public class SqliteRebuildDefinitionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DesignerRebuild_PreservesConstraintsIndexesAndIncomingForeignKeys(bool changeType)
    {
        var cs = $"Data Source=rebuild_{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Foreign Keys=True";
        await using var keepAlive = new SqliteConnection(cs);
        await keepAlive.OpenAsync();
        var provider = new RecordingSqliteProvider();
        await provider.ExecuteNonQueryAsync(cs, """
            CREATE TABLE Parent (a INTEGER, b INTEGER, PRIMARY KEY (a, b));
            CREATE TABLE Target (id INTEGER PRIMARY KEY, a INTEGER, b INTEGER, label TEXT,
                CHECK (length(label) > 0),
                FOREIGN KEY (a, b) REFERENCES Parent(a, b) ON DELETE CASCADE ON UPDATE RESTRICT);
            CREATE INDEX partial_desc ON Target(label DESC) WHERE a > 0;
            CREATE UNIQUE INDEX unique_label ON Target(label);
            CREATE INDEX expression_label ON Target(lower(label));
            CREATE TABLE Child (target_id INTEGER REFERENCES Target(id) ON DELETE CASCADE);
            INSERT INTO Parent VALUES (1, 2);
            INSERT INTO Target VALUES (3, 1, 2, 'Ada');
            INSERT INTO Child VALUES (3);
            """);
        var indexesBefore = await provider.ExecuteQueryAsync(cs,
            "SELECT name, sql FROM sqlite_master WHERE type='index' AND tbl_name='Target' ORDER BY name;");
        var vm = new TableDesignerViewModel("Target", provider, cs,
            new SchemaNode { Name = "Target", RawName = "Target", Type = SchemaNodeType.Table });
        await vm.LoadAsync();
        Assert.DoesNotContain("Error", vm.StatusMessage ?? "");
        if (changeType)
            vm.Columns.Single(c => c.Name == "label").DataType = "VARCHAR(100)";
        else
            vm.Columns.Add(new ColumnDefinition { Name = "notes", DataType = "TEXT", IsNew = true });
        vm.UpdatePreview();
        var preview = vm.PreviewSql;
        vm.Review.DestructiveConfirmed = true;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(preview, provider.LastSql);
        Assert.Contains("successfully", vm.StatusMessage);
        var indexesAfter = await provider.ExecuteQueryAsync(cs,
            "SELECT name, sql FROM sqlite_master WHERE type='index' AND tbl_name='Target' ORDER BY name;");
        Assert.Equal(indexesBefore.ObjectRows.Select(r => string.Join("|", r)),
            indexesAfter.ObjectRows.Select(r => string.Join("|", r)));
        var foreignKeys = await provider.ExecuteQueryAsync(cs, "PRAGMA foreign_key_list(Target);");
        Assert.Equal(2, foreignKeys.ObjectRows.Count);
        Assert.All(foreignKeys.ObjectRows, r =>
        {
            Assert.Equal("RESTRICT", r[foreignKeys.IndexOfColumn("on_update")]);
            Assert.Equal("CASCADE", r[foreignKeys.IndexOfColumn("on_delete")]);
        });
        var definition = await provider.ExecuteQueryAsync(cs,
            "SELECT sql FROM sqlite_master WHERE name='Target';");
        Assert.Contains("CHECK (length(label) > 0)", Assert.IsType<string>(Assert.Single(definition.ObjectRows)[0]));
        var children = await provider.ExecuteQueryAsync(cs, "SELECT target_id FROM Child;");
        Assert.Equal(3L, Assert.Single(children.ObjectRows)[0]);
        var violations = await provider.ExecuteQueryAsync(cs, "PRAGMA foreign_key_check;");
        Assert.Empty(violations.ObjectRows);
        Assert.False(vm.SupportsTableDesignerRenameColumn);
        var metadata = await provider.GetIndexesAsync(cs, "Target");
        Assert.True(metadata.Single(i => i.Name == "unique_label").IsUnique);
        Assert.Equal(IndexColumnDirection.Desc, metadata.Single(i => i.Name == "partial_desc").ColumnDirections[0]);
        Assert.Equal("a > 0", metadata.Single(i => i.Name == "partial_desc").WherePredicate);
    }

    [Fact]
    public async Task Rebuild_ForeignKeyViolationRollsBackOriginalDefinitionAndData()
    {
        var cs = $"Data Source=rebuild_{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Foreign Keys=True";
        await using var keepAlive = new SqliteConnection(cs);
        await keepAlive.OpenAsync();
        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(cs, """
            CREATE TABLE Parent (code TEXT PRIMARY KEY);
            CREATE TABLE Target (code TEXT REFERENCES Parent(code));
            INSERT INTO Parent VALUES ('001');
            INSERT INTO Target VALUES ('001');
            """);
        var before = await provider.ExecuteQueryAsync(cs, "SELECT sql FROM sqlite_master WHERE name='Target';");
        var vm = new TableDesignerViewModel("Target", provider, cs,
            new SchemaNode { Name = "Target", Type = SchemaNodeType.Table });
        await vm.LoadAsync();
        vm.Columns[0].DataType = "INTEGER";
        vm.Review.DestructiveConfirmed = true;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("foreign key integrity check", vm.StatusMessage);
        var after = await provider.ExecuteQueryAsync(cs, "SELECT sql FROM sqlite_master WHERE name='Target';");
        Assert.Equal(Assert.Single(before.ObjectRows)[0], Assert.Single(after.ObjectRows)[0]);
        var data = await provider.ExecuteQueryAsync(cs, "SELECT code FROM Target;");
        Assert.Equal("001", Assert.Single(data.ObjectRows)[0]);
        var temporary = await provider.ExecuteQueryAsync(cs, "SELECT name FROM sqlite_master WHERE name LIKE '_rain_tmp_%';");
        Assert.Empty(temporary.ObjectRows);
        await Assert.ThrowsAsync<SqliteException>(() => provider.ExecuteNonQueryAsync(cs, "INSERT INTO Target VALUES ('missing');"));
    }

    [Fact]
    public async Task Rebuild_QuotedIdentifiersAndExistingTemporaryName_DoNotCollide()
    {
        var cs = $"Data Source=rebuild_{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Foreign Keys=True";
        await using var keepAlive = new SqliteConnection(cs);
        await keepAlive.OpenAsync();
        var provider = new SqliteProvider();
        const string table = "odd \" ' [table]";
        const string column = "odd \" ' [column]";
        var builder = DbConnect.Services.Builders.DdlBuilderFactory.For("SQLite");
        var qTable = builder.Quote(table);
        var qColumn = builder.Quote(column);
        var collision = builder.Quote("_rain_tmp_" + table);
        await provider.ExecuteNonQueryAsync(cs, $"""
            CREATE TABLE {qTable} ({qColumn} TEXT CHECK (length({qColumn}) > 0), obsolete TEXT);
            CREATE INDEX "odd ' index" ON {qTable} ({qColumn} DESC) WHERE {qColumn} <> 'x,y';
            INSERT INTO {qTable} VALUES ('kept', 'removed');
            CREATE TABLE {collision} (sentinel TEXT);
            INSERT INTO {collision} VALUES ('untouched');
            """);
        var vm = new TableDesignerViewModel(table, provider, cs,
            new SchemaNode { Name = table, Type = SchemaNodeType.Table });
        await vm.LoadAsync();
        Assert.DoesNotContain("Error", vm.StatusMessage ?? "");
        vm.Columns.Single(c => c.Name == "obsolete").IsDropped = true;
        vm.Columns.Add(new ColumnDefinition { Name = "new \" [column]", DataType = "TEXT", IsNew = true });
        vm.Review.DestructiveConfirmed = true;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("successfully", vm.StatusMessage);
        var data = await provider.ExecuteQueryAsync(cs, $"SELECT {qColumn} FROM {qTable};");
        Assert.Equal("kept", Assert.Single(data.ObjectRows)[0]);
        var sentinel = await provider.ExecuteQueryAsync(cs, $"SELECT sentinel FROM {collision};");
        Assert.Equal("untouched", Assert.Single(sentinel.ObjectRows)[0]);
        var columns = await provider.GetColumnDefinitionsAsync(cs, table);
        Assert.DoesNotContain(columns, c => c.Name == "obsolete");
        Assert.Contains(columns, c => c.Name == "new \" [column]");
        Assert.Equal(IndexColumnDirection.Desc, Assert.Single(await provider.GetIndexesAsync(cs, table)).ColumnDirections[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rebuild_PreservesGeneratedColumnsCommentsAndAutoincrementSequence(bool emptyTable)
    {
        var cs = $"Data Source=rebuild_{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Foreign Keys=True";
        await using var keepAlive = new SqliteConnection(cs);
        await keepAlive.OpenAsync();
        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(cs, """
            CREATE TABLE Target (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                label TEXT -- comment must not swallow the next comma
                , upper_label TEXT GENERATED ALWAYS AS (upper(label)) VIRTUAL);
            INSERT INTO Target(id, label) VALUES (1, 'Ada'), (50, 'deleted');
            DELETE FROM Target WHERE id=50;
            """);
        var vm = new TableDesignerViewModel("Target", provider, cs,
            new SchemaNode { Name = "Target", Type = SchemaNodeType.Table });
        if (emptyTable)
            await provider.ExecuteNonQueryAsync(cs, "DELETE FROM Target;");
        await vm.LoadAsync();
        vm.Columns.Add(new ColumnDefinition { Name = "notes", DataType = "TEXT", IsNew = true });
        vm.Review.DestructiveConfirmed = true;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("successfully", vm.StatusMessage);
        await provider.ExecuteNonQueryAsync(cs, "INSERT INTO Target(label) VALUES ('Grace');");
        var rows = await provider.ExecuteQueryAsync(cs, "SELECT id, upper_label FROM Target ORDER BY id;");
        var last = rows.ObjectRows.Last();
        Assert.Equal(51L, last[0]);
        Assert.Equal("GRACE", last[1]);
        if (!emptyTable)
            Assert.Equal("ADA", rows.ObjectRows[0][1]);
    }

    [Fact]
    public async Task Rebuild_EditingNullDefaultAndNamedNullabilityRetainsCheckConstraint()
    {
        var cs = $"Data Source=rebuild_{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Foreign Keys=True";
        await using var keepAlive = new SqliteConnection(cs);
        await keepAlive.OpenAsync();
        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(cs, """
            CREATE TABLE Target (label TEXT CONSTRAINT nn NOT NULL CONSTRAINT df DEFAULT NULL CHECK (length(label) > 0));
            INSERT INTO Target(label) VALUES ('Ada');
            """);
        var vm = new TableDesignerViewModel("Target", provider, cs,
            new SchemaNode { Name = "Target", Type = SchemaNodeType.Table });
        await vm.LoadAsync();
        vm.Columns[0].IsNullable = true;
        vm.Columns[0].DefaultValue = "'Grace'";
        vm.Review.DestructiveConfirmed = true;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("successfully", vm.StatusMessage);
        await provider.ExecuteNonQueryAsync(cs, "INSERT INTO Target DEFAULT VALUES;");
        var rows = await provider.ExecuteQueryAsync(cs, "SELECT label FROM Target ORDER BY rowid;");
        Assert.Equal("Grace", rows.ObjectRows[1][0]);
        var definition = await provider.ExecuteQueryAsync(cs, "SELECT sql FROM sqlite_master WHERE name='Target';");
        Assert.DoesNotContain("CONSTRAINT nn", Assert.IsType<string>(Assert.Single(definition.ObjectRows)[0]));
        await Assert.ThrowsAsync<SqliteException>(() => provider.ExecuteNonQueryAsync(cs,
            "INSERT INTO Target VALUES ('');"));
    }

    [Fact]
    public async Task Designer_DoesNotRebuildAfterIndexMetadataFailsToLoad()
    {
        var cs = $"Data Source=rebuild_{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Foreign Keys=True";
        await using var keepAlive = new SqliteConnection(cs);
        await keepAlive.OpenAsync();
        var provider = new RecordingSqliteProvider();
        const string setup = "CREATE TABLE Target (label TEXT); CREATE INDEX ix_label ON Target(label);";
        await provider.ExecuteNonQueryAsync(cs, setup);
        var vm = new TableDesignerViewModel("Target", provider, cs,
            new SchemaNode { Name = "Target", Type = SchemaNodeType.Table });
        await vm.LoadAsync();
        provider.FailIndexLoad = true;
        await vm.LoadAsync();
        vm.Columns.Add(new ColumnDefinition { Name = "notes", DataType = "TEXT", IsNew = true });
        vm.Review.DestructiveConfirmed = true;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("must be loaded", vm.StatusMessage);
        Assert.Equal(setup, provider.LastSql);
        var indexes = await provider.ExecuteQueryAsync(cs, "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='Target';");
        Assert.Equal("ix_label", Assert.Single(indexes.ObjectRows)[0]);
    }

    private sealed class RecordingSqliteProvider : SqliteProvider, IDatabaseProvider
    {
        public string? LastSql { get; private set; }
        public bool FailIndexLoad { get; set; }

        public new Task<List<IndexDefinition>> GetIndexesAsync(string connectionString, string tableName) =>
            FailIndexLoad ? throw new InvalidOperationException("Index metadata unavailable.")
                : base.GetIndexesAsync(connectionString, tableName);

        public new Task<int> ExecuteNonQueryAsync(string connectionString, string sql)
        {
            LastSql = sql;
            return base.ExecuteNonQueryAsync(connectionString, sql);
        }
    }
}
