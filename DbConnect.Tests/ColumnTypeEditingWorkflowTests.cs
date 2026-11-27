using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>
/// Issue 09: column length, precision, scale and collation through the designer's public
/// load/edit/preview/save workflow against real SQLite and DuckDB databases. A loaded type is
/// split into its type name and modifiers, edits are validated by the provider's rules, Save runs
/// the previewed plan, and the reopened table shows the saved settings.
/// </summary>
public sealed class ColumnTypeEditingWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rain-types-" + Guid.NewGuid().ToString("N"));

    public ColumnTypeEditingWorkflowTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private async Task<(string Cs, SqliteProvider Provider)> SqliteAsync(string sql)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "types.db"), Pooling = false }.ToString();
        var provider = new SqliteProvider();
        await provider.ExecuteNonQueryAsync(cs, sql);
        return (cs, provider);
    }

    private string DuckDb(string sql)
    {
        var cs = $"Data Source={Path.Combine(_directory, "types.duckdb")}";
        using var connection = new DuckDBConnection(cs);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        return cs;
    }

    private static async Task<TableDesignerViewModel> OpenAsync(IDatabaseProvider provider, string cs, string table, string? schema = null)
    {
        var vm = new TableDesignerViewModel(table, new DatabaseSession(
                new ConnectionInfo { Name = "types", Provider = provider.ProviderName, ConnectionString = cs }, provider),
            new SchemaNode { Name = table, RawName = table, Type = SchemaNodeType.Table },
            new DatabaseObjectIdentity(null, schema, table, SchemaNodeType.Table));
        await vm.LoadAsync();
        Assert.True(vm.Design.IsLoaded, vm.Design.Error ?? vm.StatusMessage);
        return vm;
    }

    private static async Task SaveAsync(TableDesignerViewModel vm)
    {
        Assert.True(vm.PlanAllowsSave || vm.Review.NeedsConfirmation, string.Join("\n", vm.ValidationErrors));
        vm.Review.DestructiveConfirmed = vm.Review.NeedsConfirmation;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.StatusMessage?.Contains("successfully") == true, vm.StatusMessage + "\n" + vm.PreviewSql);
        Assert.Equal("", vm.PreviewSql);
    }

    [Fact]
    public async Task Sqlite_LoadsModifiersAndCollation_AndSavedEditsReopen()
    {
        var (cs, provider) = await SqliteAsync("""
            CREATE TABLE t (id INTEGER PRIMARY KEY, price DECIMAL(10,2) NOT NULL DEFAULT 0 CHECK (price >= 0),
                            label VARCHAR(20) COLLATE NOCASE, note TEXT);
            CREATE INDEX t_label ON t(label);
            CREATE TRIGGER t_touch AFTER UPDATE ON t BEGIN SELECT 1; END;
            INSERT INTO t VALUES (1, 9.5, 'Ada', 'kept');
            """);
        var vm = await OpenAsync(provider, cs, "t");
        var price = vm.Columns.Single(c => c.Name == "price");
        var label = vm.Columns.Single(c => c.Name == "label");
        Assert.Equal(("DECIMAL", 10, 2), (price.DataType, price.Precision, price.Scale));
        Assert.Equal(("VARCHAR", "20", "NOCASE"), (label.DataType, label.Length, label.Collation));
        Assert.Equal("", vm.PreviewSql);

        price.Precision = 12;
        label.Length = "40";
        label.Collation = "RTRIM";
        Assert.Contains("price DECIMAL(12,2) NOT NULL DEFAULT 0 CHECK (price >= 0)", vm.PreviewSql);
        Assert.Contains("label VARCHAR(40) COLLATE RTRIM", vm.PreviewSql);
        await SaveAsync(vm);

        var reopened = await OpenAsync(provider, cs, "t");
        price = reopened.Columns.Single(c => c.Name == "price");
        label = reopened.Columns.Single(c => c.Name == "label");
        Assert.Equal(("DECIMAL", 12, 2), (price.DataType, price.Precision, price.Scale));
        Assert.Equal(("VARCHAR", "40", "RTRIM"), (label.DataType, label.Length, label.Collation));
        Assert.Equal(["t_touch"], reopened.Design.Triggers.Select(t => t.Name));
        Assert.Contains(reopened.Indexes.Items, i => i.Name == "t_label");
        var row = Assert.Single((await provider.ExecuteQueryAsync(cs, "SELECT price, label, note FROM t;")).ObjectRows);
        Assert.Equal(["9.5", "Ada", "kept"], row.Select(v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task DuckDb_WideningSavesWithoutReview_NarrowingAndCollationNeedConfirmation_AndBothReopen()
    {
        var cs = DuckDb("""
            CREATE SCHEMA "s.x";
            CREATE TABLE "s.x".t (id INTEGER PRIMARY KEY, qty DECIMAL(10,2) NOT NULL DEFAULT 1, name VARCHAR COLLATE NOCASE, note VARCHAR);
            COMMENT ON COLUMN "s.x".t.note IS 'kept';
            INSERT INTO "s.x".t VALUES (1, 12.5, 'Ada', 'n');
            """);
        var provider = new DuckDbProvider();
        var vm = await OpenAsync(provider, cs, "t", "s.x");
        var qty = vm.Columns.Single(c => c.Name == "qty");
        var name = vm.Columns.Single(c => c.Name == "name");
        Assert.Equal(("DECIMAL", 10, 2), (qty.DataType, qty.Precision, qty.Scale));
        Assert.Equal(("VARCHAR", null, "NOCASE"), (name.DataType, name.Length, name.Collation));

        qty.Precision = 14;
        qty.Scale = 4;
        Assert.Equal("""ALTER TABLE "s.x"."t" ALTER COLUMN "qty" TYPE DECIMAL(14,4);""", vm.PreviewSql);
        Assert.Empty(vm.Plan.Destructive);
        await SaveAsync(vm);
        Assert.Equal(("DECIMAL", 14, 4), (qty.DataType, qty.Precision, qty.Scale));

        qty = vm.Columns.Single(c => c.Name == "qty");
        name = vm.Columns.Single(c => c.Name == "name");
        qty.Scale = 1;
        name.Collation = null;
        vm.Columns.Single(c => c.Name == "note").Collation = "NOCASE";
        Assert.Equal(3, vm.Plan.Destructive.Count);
        Assert.Contains("Changes the type of qty from DECIMAL(14,4) to DECIMAL(14,1)", vm.Plan.Destructive[0]);
        Assert.Contains("Changes the collation of name from NOCASE to the default", vm.Plan.Destructive[1]);
        Assert.False(vm.PlanAllowsSave);
        await SaveAsync(vm);

        var reopened = await OpenAsync(provider, cs, "t", "s.x");
        Assert.Equal("DECIMAL(14,1)", reopened.Design.Baseline!.Columns.Single(c => c.Name == "qty").DataType);
        Assert.Null(reopened.Columns.Single(c => c.Name == "name").Collation);
        Assert.Equal("NOCASE", reopened.Columns.Single(c => c.Name == "note").Collation);
        Assert.Equal("kept", reopened.Design.Baseline.Columns.Single(c => c.Name == "note").Comment);
        Assert.Equal("1", reopened.Design.Baseline.Columns.Single(c => c.Name == "qty").Default?.Trim('\''));
        var row = Assert.Single((await provider.ExecuteQueryAsync(cs, "SELECT CAST(qty AS VARCHAR), name FROM \"s.x\".t;")).ObjectRows);
        Assert.Equal(["12.5", "Ada"], row);
    }

    [Theory]
    [InlineData("qty", "Precision", "39", "The precision of column \"qty\" must be between 1 and 38 for DuckDB type DECIMAL.")]
    [InlineData("qty", "Scale", "11", "The scale of column \"qty\" must be between 0 and 10 (no more than the precision).")]
    [InlineData("qty", "Length", "5", "Column \"qty\": DuckDB type DECIMAL takes a precision and scale, not a length.")]
    [InlineData("name", "Length", "5", "Column \"name\": DuckDB type VARCHAR takes no length, precision or scale.")]
    [InlineData("id", "Collation", "NOCASE", "Column \"id\": DuckDB type INTEGER has no collation; only character types do.")]
    [InlineData("name", "Collation", "NOCASE; DROP TABLE t", "The collation of column \"name\" must be a collation name.")]
    [InlineData("name", "DataType", "VARCHAR(4)", null)]
    public async Task DuckDb_InvalidModifiersAndCollationsAreRejectedBeforeAnythingRuns(string column, string field, string value, string? error)
    {
        var cs = DuckDb("CREATE TABLE t (id INTEGER, qty DECIMAL(10,2), name VARCHAR);");
        var provider = new DuckDbProvider();
        var vm = await OpenAsync(provider, cs, "t");
        var target = vm.Columns.Single(c => c.Name == column);
        switch (field)
        {
            case "Precision": target.Precision = int.Parse(value); break;
            case "Scale": target.Scale = int.Parse(value); break;
            case "Length": target.Length = value; break;
            case "Collation": target.Collation = value; break;
            default: target.DataType = value; target.Precision = 3; break;
        }
        var expected = error ?? "Column \"name\" gives modifiers both in its type and in the Length, Precision or Scale fields; use one or the other.";
        Assert.Contains(expected, vm.ValidationErrors);
        Assert.False(vm.PlanAllowsSave);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains(expected, vm.StatusMessage);
        var reopened = await OpenAsync(provider, cs, "t");
        Assert.Equal(["id INTEGER", "qty DECIMAL(10,2)", "name VARCHAR"],
            reopened.Design.Baseline!.Columns.Select(c => $"{c.Name} {c.DataType}{(c.Collation == null ? "" : " " + c.Collation)}"));
    }

    [Theory]
    [InlineData("SQLite")]
    [InlineData("DuckDB")]
    public async Task NewTable_CreatesModifiersAndCollation_AndReopensWithThem(string providerName)
    {
        string cs;
        IDatabaseProvider provider;
        if (providerName == "SQLite")
            (cs, provider) = await SqliteAsync("CREATE TABLE seed (id INTEGER);");
        else
        {
            cs = DuckDb("CREATE TABLE seed (id INTEGER);");
            provider = new DuckDbProvider();
        }
        var vm = new TableDesignerViewModel("made", new DatabaseSession(
            new ConnectionInfo { Name = "types", Provider = provider.ProviderName, ConnectionString = cs }, provider));
        vm.Columns.Add(new ColumnDefinition { Name = "id", DataType = "INTEGER", IsNullable = false, IsPrimaryKey = true, IsNew = true });
        vm.Columns.Add(new ColumnDefinition { Name = "amount", DataType = "DECIMAL", Precision = 9, Scale = 3, IsNew = true });
        vm.Columns.Add(new ColumnDefinition { Name = "label", DataType = "VARCHAR", Collation = "NOCASE", IsNew = true });
        Assert.Contains("\"amount\" DECIMAL(9,3)", vm.PreviewSql);
        Assert.Contains("\"label\" VARCHAR COLLATE NOCASE", vm.PreviewSql);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.StatusMessage?.Contains("successfully") == true, vm.StatusMessage + "\n" + vm.PreviewSql);

        var reopened = await OpenAsync(provider, cs, "made");
        var amount = reopened.Columns.Single(c => c.Name == "amount");
        Assert.Equal(("DECIMAL", 9, 3), (amount.DataType, amount.Precision, amount.Scale));
        Assert.Equal("NOCASE", reopened.Columns.Single(c => c.Name == "label").Collation);
        Assert.Equal("", reopened.PreviewSql);
    }
}
