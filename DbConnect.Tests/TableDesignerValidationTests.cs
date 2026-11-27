using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public class TableDesignerValidationTests
{
    private static TableDesignerViewModel CreateVm()
    {
        var provider = new SqliteProvider();
        var conn = new ConnectionInfo { Name = "test", Provider = "SQLite", ConnectionString = "Data Source=:memory:" };
        var session = new DatabaseSession(conn, provider);
        return new TableDesignerViewModel("test_table", session);
    }

    private static ColumnDefinition Col(string name) =>
        new() { Name = name, DataType = "TEXT", IsNew = true };

    [Fact]
    public void Validation_NoColumns_ReturnsError()
    {
        var vm = CreateVm();
        // No columns added

        var errors = vm.ValidateDesigner();

        Assert.Contains(errors, e => e.Contains("At least one column"));
    }

    [Fact]
    public void Validation_EmptyTableName_ReturnsError()
    {
        var vm = CreateVm();
        vm.Columns.Add(Col("id"));
        vm.TableName = "";

        var errors = vm.ValidateDesigner();

        Assert.Contains(errors, e => e.Contains("Table name cannot be empty"));
    }

    [Fact]
    public void Validation_DuplicateColumnName_ReturnsError()
    {
        var vm = CreateVm();
        vm.Columns.Add(Col("id"));
        vm.Columns.Add(Col("ID")); // duplicate (case-insensitive)

        var errors = vm.ValidateDesigner();

        Assert.Contains(errors, e => e.Contains("Duplicate column name"));
    }

    [Fact]
    public void Validation_BlankColumnName_ReturnsError()
    {
        var vm = CreateVm();
        vm.Columns.Add(Col(""));

        var errors = vm.ValidateDesigner();

        Assert.Contains(errors, e => e.Contains("All columns must have a name"));
    }

    [Fact]
    public void Validation_DuplicateIndexName_ReturnsError()
    {
        var vm = CreateVm();
        vm.Columns.Add(Col("id"));
        vm.AddIndex("idx_1", false, "id");
        vm.AddIndex("IDX_1", true, "id");

        var errors = vm.ValidateDesigner();

        Assert.Contains(errors, e => e.Contains("Duplicate index name"));
    }

    [Fact]
    public void Validation_IndexWithNoColumns_ReturnsError()
    {
        var vm = CreateVm();
        vm.Columns.Add(Col("id"));
        vm.AddIndex("idx_empty", false);

        var errors = vm.ValidateDesigner();

        Assert.Contains(errors, e => e.Contains("has no key columns"));
    }

    private static EditableForeignKey Fk(string? name, string column, string? table, string referenced)
    {
        var fk = new EditableForeignKey
        {
            Name = name,
            ReferencedTable = table == null ? null : new DatabaseObjectIdentity(null, null, table, SchemaNodeType.Table)
        };
        fk.Pairs.Add(new ForeignKeyPair { Column = column, ReferencedColumn = referenced });
        return fk;
    }

    /// <summary>Issue 12: a blank name lets the engine name the foreign key.</summary>
    [Fact]
    public void Validation_BlankFkConstraintName_LetsTheEngineNameIt()
    {
        var vm = CreateVm();
        vm.Columns.Add(Col("id"));
        vm.ForeignKeys.Items.Add(Fk("", "id", "test_table", "id"));

        var errors = vm.ValidateDesigner();

        Assert.DoesNotContain(errors, e => e.Contains("must have a name"));
    }

    [Fact]
    public void Validation_DuplicateFkConstraintName_ReturnsError()
    {
        var vm = CreateVm();
        vm.Columns.Add(Col("id"));
        vm.ForeignKeys.Items.Add(Fk("FK_A", "id", "T", "id"));
        vm.ForeignKeys.Items.Add(Fk("fk_a", "id", "T", "id"));

        var errors = vm.ValidateDesigner();

        Assert.Contains(errors, e => e.Contains("Duplicate foreign key constraint name"));
    }

    [Fact]
    public void Validation_FkMissingReferencedTable_ReturnsError()
    {
        var vm = CreateVm();
        vm.Columns.Add(Col("id"));
        vm.ForeignKeys.Items.Add(Fk("FK_Test", "id", null, "id"));

        var errors = vm.ValidateDesigner();

        Assert.Contains(errors, e => e.Contains("has no referenced table"));
    }

    /// <summary>Issue 13: a blank name lets the engine name the check.</summary>
    [Fact]
    public void Validation_BlankCheckConstraintName_LetsTheEngineNameIt()
    {
        var vm = CreateVm();
        vm.Columns.Add(Col("price"));
        vm.Checks.Items.Add(new EditableCheck { Name = "", Expression = "price > 0" });

        var errors = vm.ValidateDesigner();

        Assert.DoesNotContain(errors, e => e.Contains("must have a name"));
    }

    [Fact]
    public void Validation_BlankCheckExpression_ReturnsError()
    {
        var vm = CreateVm();
        vm.Columns.Add(Col("price"));
        vm.Checks.Items.Add(new EditableCheck { Name = "chk_price", Expression = "" });

        var errors = vm.ValidateDesigner();

        Assert.Contains(errors, e => e.Contains("Check chk_price has no expression"));
    }

    [Fact]
    public void Validation_DuplicateCheckConstraintName_ReturnsError()
    {
        var vm = CreateVm();
        vm.Columns.Add(Col("price"));
        vm.Checks.Items.Add(new EditableCheck { Name = "CHK_A", Expression = "price > 0" });
        vm.Checks.Items.Add(new EditableCheck { Name = "chk_a", Expression = "price < 100" });

        var errors = vm.ValidateDesigner();

        Assert.Contains(errors, e => e.Contains("Duplicate check constraint name"));
    }

    [Fact]
    public void Validation_ValidDesigner_ReturnsNoErrors()
    {
        var vm = CreateVm();
        vm.Columns.Add(Col("id"));
        vm.Columns.Add(Col("name"));
        vm.AddIndex("idx_name", false, "name");

        var errors = vm.ValidateDesigner();

        Assert.Empty(errors);
    }
}
