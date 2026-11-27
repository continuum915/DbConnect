using Microsoft.Data.Sqlite;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public class SqliteRebuildObjectsTests
{
    [Fact]
    public async Task AddingColumn_PreservesTriggersAndDependentViews()
    {
        var cs = $"Data Source=objects_{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Foreign Keys=True";
        await using var keepAlive = new SqliteConnection(cs);
        await keepAlive.OpenAsync();
        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(cs, """
            CREATE TABLE Target (id INTEGER PRIMARY KEY, label TEXT);
            CREATE TABLE Audit (label TEXT);
            CREATE TRIGGER log_label AFTER INSERT ON Target BEGIN
                INSERT INTO Audit VALUES (NEW.label);
            END;
            CREATE VIEW labels AS SELECT label FROM Target;
            CREATE VIEW nested_labels AS SELECT label FROM labels;
            INSERT INTO Target VALUES (1, 'Ada');
            """);
        var vm = new TableDesignerViewModel("Target", provider, cs,
            new SchemaNode { Name = "Target", Type = SchemaNodeType.Table });
        await vm.LoadAsync();
        vm.Columns.Add(new ColumnDefinition { Name = "notes", DataType = "TEXT", IsNew = true });
        vm.Review.DestructiveConfirmed = true;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.StatusMessage?.Contains("successfully") == true, vm.StatusMessage);
        await provider.ExecuteNonQueryAsync(cs, "INSERT INTO Target(id, label) VALUES (2, 'Grace');");
        var audit = await provider.ExecuteQueryAsync(cs, "SELECT label FROM Audit ORDER BY rowid;");
        Assert.Equal(new[] { "Ada", "Grace" }, audit.ObjectRows.Select(r => r[0]));
        var rows = await provider.ExecuteQueryAsync(cs, "SELECT label FROM nested_labels ORDER BY label;");
        Assert.Equal(new[] { "Ada", "Grace" }, rows.ObjectRows.Select(r => r[0]));
    }

    [Fact]
    public async Task DroppingColumn_RemovesOnlyDependentObjectsAndReportsThemInPreview()
    {
        var cs = $"Data Source=objects_{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Foreign Keys=True";
        await using var keepAlive = new SqliteConnection(cs);
        await keepAlive.OpenAsync();
        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(cs, """
            CREATE TABLE Parent (code INTEGER PRIMARY KEY);
            INSERT INTO Parent VALUES (1), (2);
            CREATE TABLE Target (id INTEGER PRIMARY KEY, label TEXT CHECK(length(label)>0),
                obsolete INTEGER CONSTRAINT inline_check CHECK(obsolete>=0) CONSTRAINT inline_fk REFERENCES Parent(code),
                kept INTEGER DEFAULT 1 CONSTRAINT cross_check CHECK(kept>obsolete),
                CONSTRAINT removed_check CHECK(obsolete<10), CONSTRAINT kept_check CHECK(kept>0),
                CONSTRAINT removed_fk FOREIGN KEY(obsolete) REFERENCES Parent(code),
                CONSTRAINT kept_fk FOREIGN KEY(kept) REFERENCES Parent(code),
                CONSTRAINT removed_unique UNIQUE(obsolete, label));
            CREATE TABLE Audit (label TEXT);
            CREATE INDEX removed_expression ON Target(obsolete+1);
            CREATE INDEX removed_partial ON Target(label) WHERE obsolete>0;
            CREATE INDEX kept_index ON Target(label) WHERE label<>'obsolete';
            CREATE TRIGGER removed_trigger AFTER INSERT ON Target BEGIN INSERT INTO Audit VALUES (NEW.obsolete); END;
            CREATE TRIGGER removed_update AFTER UPDATE OF obsolete ON Target BEGIN INSERT INTO Audit VALUES ('updated'); END;
            CREATE TRIGGER kept_trigger AFTER INSERT ON Target BEGIN INSERT INTO Audit VALUES (NEW.label); END;
            CREATE VIEW removed_view AS SELECT t.obsolete FROM Target AS t;
            CREATE VIEW removed_nested AS SELECT * FROM removed_view;
            CREATE VIEW kept_view AS SELECT label AS obsolete FROM Target;
            INSERT INTO Target VALUES (1, 'Ada', 1, 2);
            """);
        await provider.ExecuteNonQueryAsync(cs, "DELETE FROM Audit;");
        var vm = new TableDesignerViewModel("Target", provider, cs,
            new SchemaNode { Name = "Target", Type = SchemaNodeType.Table });
        await vm.LoadAsync();
        vm.Columns.Single(c => c.Name == "obsolete").IsDropped = true;
        vm.UpdatePreview();
        var comments = vm.PreviewSql.Split("-- SQLite rebuild", StringSplitOptions.None)[0];
        foreach (var name in new[] { "inline_check", "inline_fk", "cross_check", "removed_check", "removed_fk",
            "removed_unique", "removed_expression", "removed_partial", "removed_trigger", "removed_update", "removed_view", "removed_nested" })
            Assert.Contains(name, comments);
        Assert.All(comments.Split('\n', StringSplitOptions.RemoveEmptyEntries), line => Assert.StartsWith("--", line));
        vm.Review.DestructiveConfirmed = true;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.StatusMessage?.Contains("successfully") == true, vm.StatusMessage);
        var schema = await provider.ExecuteQueryAsync(cs, "SELECT name FROM sqlite_master WHERE type IN ('index','trigger','view');");
        Assert.Equal(new[] { "kept_index", "kept_trigger", "kept_view" }, schema.ObjectRows.Select(r => (string)r[0]!).OrderBy(n => n));
        var foreignKeys = await provider.ExecuteQueryAsync(cs, "PRAGMA foreign_key_list(Target);");
        Assert.Equal("kept", Assert.Single(foreignKeys.ObjectRows)[foreignKeys.IndexOfColumn("from")]);
        await provider.ExecuteNonQueryAsync(cs, "INSERT INTO Target(id, label, kept) VALUES (2, 'Grace', 1);");
        var audit = await provider.ExecuteQueryAsync(cs, "SELECT label FROM Audit;");
        Assert.Equal("Grace", Assert.Single(audit.ObjectRows)[0]);
        var view = await provider.ExecuteQueryAsync(cs, "SELECT obsolete FROM kept_view ORDER BY obsolete;");
        Assert.Equal(new[] { "Ada", "Grace" }, view.ObjectRows.Select(r => r[0]));
        await Assert.ThrowsAsync<SqliteException>(() => provider.ExecuteNonQueryAsync(cs,
            "INSERT INTO Target(id, label, kept) VALUES (3, '', 1);"));
        await Assert.ThrowsAsync<SqliteException>(() => provider.ExecuteNonQueryAsync(cs,
            "INSERT INTO Target(id, label, kept) VALUES (3, 'bad', 0);"));
    }

    [Fact]
    public async Task FailedForeignKeyCheck_RestoresViewsTriggersAndOriginalData()
    {
        var cs = $"Data Source=objects_{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Foreign Keys=True";
        await using var keepAlive = new SqliteConnection(cs);
        await keepAlive.OpenAsync();
        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(cs, """
            CREATE TABLE Parent (code TEXT PRIMARY KEY);
            CREATE TABLE Target (code TEXT REFERENCES Parent(code));
            CREATE TABLE Audit (code TEXT);
            CREATE TRIGGER log_code AFTER INSERT ON Target BEGIN INSERT INTO Audit VALUES (NEW.code); END;
            CREATE VIEW codes AS SELECT code FROM Target;
            CREATE TRIGGER insert_code INSTEAD OF INSERT ON codes BEGIN INSERT INTO Target VALUES (NEW.code); END;
            INSERT INTO Parent VALUES ('001');
            INSERT INTO Target VALUES ('001');
            """);
        var before = await provider.ExecuteQueryAsync(cs,
            "SELECT name, sql FROM sqlite_master WHERE type IN ('trigger','view') ORDER BY name;");
        var vm = new TableDesignerViewModel("Target", provider, cs,
            new SchemaNode { Name = "Target", Type = SchemaNodeType.Table });
        await vm.LoadAsync();
        vm.Columns[0].DataType = "INTEGER";
        vm.Review.DestructiveConfirmed = true;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("foreign key integrity check", vm.StatusMessage);
        var after = await provider.ExecuteQueryAsync(cs,
            "SELECT name, sql FROM sqlite_master WHERE type IN ('trigger','view') ORDER BY name;");
        Assert.Equal(before.ObjectRows.Select(r => string.Join("|", r)), after.ObjectRows.Select(r => string.Join("|", r)));
        var rows = await provider.ExecuteQueryAsync(cs, "SELECT code FROM codes;");
        Assert.Equal("001", Assert.Single(rows.ObjectRows)[0]);
        await provider.ExecuteNonQueryAsync(cs, "INSERT INTO codes VALUES ('001');");
        var audit = await provider.ExecuteQueryAsync(cs, "SELECT code FROM Audit;");
        Assert.Equal(new[] { "001", "001" }, audit.ObjectRows.Select(r => r[0]));
        await Assert.ThrowsAsync<SqliteException>(() => provider.ExecuteNonQueryAsync(cs,
            "INSERT INTO codes VALUES ('missing');"));
    }

    [Fact]
    public async Task DroppingQuotedColumn_PreservesUnrelatedNamesAndViewTriggers()
    {
        var cs = $"Data Source=objects_{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Foreign Keys=True";
        await using var keepAlive = new SqliteConnection(cs);
        await keepAlive.OpenAsync();
        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(cs, """
            CREATE TABLE "odd target" (id INTEGER PRIMARY KEY, label TEXT, "old value" TEXT);
            CREATE TABLE Other ("old value" TEXT);
            CREATE TABLE Audit (label TEXT);
            CREATE TRIGGER "old value" AFTER INSERT ON "odd target" BEGIN
                INSERT INTO Audit VALUES (NEW.label || 'old value');
            END;
            CREATE VIEW "old value" AS SELECT label AS "old value" FROM "odd target";
            CREATE TRIGGER view_insert INSTEAD OF INSERT ON "old value" BEGIN
                INSERT INTO "odd target"(label) VALUES (NEW."old value");
            END;
            CREATE VIEW joined_view AS SELECT o."old value" FROM Other o JOIN "odd target" t ON t.label=o."old value";
            CREATE TRIGGER external_read AFTER DELETE ON Other BEGIN
                INSERT INTO Audit SELECT t."old value" FROM "odd target" AS t;
            END;
            CREATE TRIGGER external_write AFTER INSERT ON Other BEGIN
                INSERT INTO "odd target"(label, "old value") VALUES (NEW."old value", 'removed');
            END;
            CREATE TRIGGER external_positional AFTER UPDATE ON Other BEGIN
                INSERT INTO "odd target" VALUES (10, NEW."old value", 'removed');
            END;
            INSERT INTO "odd target" VALUES (1, 'Ada', 'obsolete');
            """);
        var vm = new TableDesignerViewModel("odd target", provider, cs,
            new SchemaNode { Name = "odd target", Type = SchemaNodeType.Table });
        await vm.LoadAsync();
        vm.Columns.Single(c => c.Name == "old value").IsDropped = true;
        vm.Review.DestructiveConfirmed = true;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.StatusMessage?.Contains("successfully") == true, vm.StatusMessage);
        var triggers = await provider.ExecuteQueryAsync(cs, "SELECT name FROM sqlite_master WHERE type='trigger' ORDER BY name;");
        Assert.Equal(new[] { "old value", "view_insert" }, triggers.ObjectRows.Select(r => r[0]));
        await provider.ExecuteNonQueryAsync(cs, """
            INSERT INTO "old value" VALUES ('Grace');
            INSERT INTO Other VALUES ('Ada');
            """);
        var rows = await provider.ExecuteQueryAsync(cs, "SELECT * FROM joined_view;");
        Assert.Equal("Ada", Assert.Single(rows.ObjectRows)[0]);
        var audit = await provider.ExecuteQueryAsync(cs, "SELECT label FROM Audit ORDER BY rowid;");
        Assert.Equal(new[] { "Adaold value", "Graceold value" }, audit.ObjectRows.Select(r => r[0]));
    }

    [Fact]
    public async Task DroppingColumn_DoesNotRemoveCollationsOrCastTypesWithTheSameName()
    {
        var cs = $"Data Source=objects_{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Foreign Keys=True";
        await using var keepAlive = new SqliteConnection(cs);
        await keepAlive.OpenAsync();
        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(cs, """
            CREATE TABLE Target (label TEXT, nocase TEXT, "TEXT" TEXT,
                CONSTRAINT collation_check CHECK(label COLLATE nocase <> 'invalid'),
                CONSTRAINT cast_check CHECK(CAST(label AS TEXT) <> 'invalid'));
            CREATE INDEX collation_index ON Target(label COLLATE nocase);
            CREATE INDEX cast_index ON Target(CAST(label AS TEXT));
            INSERT INTO Target VALUES ('Ada', 'drop', 'drop');
            """);
        var vm = new TableDesignerViewModel("Target", provider, cs,
            new SchemaNode { Name = "Target", Type = SchemaNodeType.Table });
        await vm.LoadAsync();
        vm.Columns.Single(c => c.Name == "nocase").IsDropped = true;
        vm.Columns.Single(c => c.Name == "TEXT").IsDropped = true;
        vm.Review.DestructiveConfirmed = true;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.StatusMessage?.Contains("successfully") == true, vm.StatusMessage);
        var indexes = await provider.GetIndexesAsync(cs, "Target");
        Assert.Equal(new[] { "cast_index", "collation_index" }, indexes.Select(i => i.Name).OrderBy(n => n));
        await Assert.ThrowsAsync<SqliteException>(() => provider.ExecuteNonQueryAsync(cs, "INSERT INTO Target VALUES ('INVALID');"));
        await Assert.ThrowsAsync<SqliteException>(() => provider.ExecuteNonQueryAsync(cs, "INSERT INTO Target VALUES ('invalid');"));
    }

    [Fact]
    public async Task AddingColumn_PreservesUnrelatedInvalidViewsAndTriggers()
    {
        var cs = $"Data Source=objects_{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Foreign Keys=True";
        await using var keepAlive = new SqliteConnection(cs);
        await keepAlive.OpenAsync();
        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(cs, """
            CREATE TABLE Target (label TEXT);
            CREATE VIEW labels AS SELECT label FROM Target;
            CREATE VIEW broken_view AS SELECT missing FROM nowhere;
            CREATE VIEW broken_nested AS SELECT * FROM broken_view;
            CREATE TABLE Other (label TEXT);
            CREATE TRIGGER broken_trigger AFTER INSERT ON Other BEGIN INSERT INTO nowhere VALUES (NEW.label); END;
            INSERT INTO Target VALUES ('Ada');
            """);
        var before = await provider.ExecuteQueryAsync(cs,
            "SELECT name, sql FROM sqlite_master WHERE name LIKE 'broken_%' ORDER BY name;");
        var vm = new TableDesignerViewModel("Target", provider, cs,
            new SchemaNode { Name = "Target", Type = SchemaNodeType.Table });
        await vm.LoadAsync();
        Assert.DoesNotContain("Error", vm.StatusMessage ?? "");
        vm.Columns.Add(new ColumnDefinition { Name = "notes", DataType = "TEXT", IsNew = true });
        vm.Review.DestructiveConfirmed = true;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.StatusMessage?.Contains("successfully") == true, vm.StatusMessage);
        var after = await provider.ExecuteQueryAsync(cs,
            "SELECT name, sql FROM sqlite_master WHERE name LIKE 'broken_%' ORDER BY name;");
        Assert.Equal(before.ObjectRows.Select(r => string.Join("|", r)), after.ObjectRows.Select(r => string.Join("|", r)));
        var rows = await provider.ExecuteQueryAsync(cs, "SELECT label FROM labels;");
        Assert.Equal("Ada", Assert.Single(rows.ObjectRows)[0]);
    }
}
