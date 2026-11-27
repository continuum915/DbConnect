using System;
using System.Linq;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public class DataChangeTrackerTests
{
    [Fact]
    public void BuildStatement_ForInsertedRow_GeneratesInsertWithEscapedValues()
    {
        var row = new EditableRow(new()
        {
            ["Id"] = 1,
            ["Name"] = "O'Brien",
            ["IsActive"] = true,
            ["Notes"] = null
        }, editedColumns: ["Id", "Name", "IsActive", "Notes"])
        {
            State = EditState.Inserted
        };
        var tracker = new DataChangeTracker("Customers", ["Id", "Name", "IsActive", "Notes"], ["Id"]);

        var sql = tracker.BuildStatement(row);

        Assert.Equal(
            """INSERT INTO "Customers" ("Id", "Name", "IsActive", "Notes") VALUES (1, 'O''Brien', 1, NULL);""",
            sql);
    }

    [Fact]
    public void SchemaQualifiedTable_QuotesSchemaAndTableSeparately()
    {
        // RAIN-P2-16 — an editable query result may come from a table in a named schema.
        var row = new EditableRow(new() { ["id"] = 1, ["name"] = "Ada" });
        row.Values["name"] = "Grace";
        row.State = EditState.Modified;
        var tracker = new DataChangeTracker("orders", ["id", "name"], ["id"], schemaName: "sales");

        Assert.Equal("""UPDATE "sales"."orders" SET "name" = 'Grace' WHERE "id" = 1 AND "name" = 'Ada';""", tracker.BuildStatement(row));
        Assert.StartsWith("""UPDATE "sales"."orders" SET""", tracker.BuildCommand(row)!.CommandText);
        Assert.StartsWith("""SELECT * FROM "sales"."orders" WHERE""", tracker.BuildRowQuery(row).Sql);
    }

    [Fact]
    public void BuildStatement_ForInsertedRow_OmitsColumnsTheUserNeverTouched()
    {
        // Id is identity and Amount has a server default; the user only typed a Name.
        var row = new EditableRow(new()
        {
            ["Id"] = null,
            ["Name"] = "New Customer",
            ["Amount"] = null
        }, editedColumns: ["Name"])
        {
            State = EditState.Inserted
        };
        var tracker = new DataChangeTracker("Customers", ["Id", "Name", "Amount"], ["Id"]);

        var sql = tracker.BuildStatement(row);

        Assert.Equal("""INSERT INTO "Customers" ("Name") VALUES ('New Customer');""", sql);
    }

    [Fact]
    public void BuildStatement_ForInsertedRow_IncludesIdentityOrDefaultColumnWhenExplicitlySet()
    {
        var row = new EditableRow(new()
        {
            ["Id"] = 99,
            ["Name"] = "New Customer",
            ["Amount"] = 5
        }, editedColumns: ["Id", "Name", "Amount"])
        {
            State = EditState.Inserted
        };
        var tracker = new DataChangeTracker("Customers", ["Id", "Name", "Amount"], ["Id"]);

        var sql = tracker.BuildStatement(row);

        Assert.Equal(
            """INSERT INTO "Customers" ("Id", "Name", "Amount") VALUES (99, 'New Customer', 5);""",
            sql);
    }

    [Fact]
    public void BuildStatement_ForInsertedRow_WithNoEditedColumns_UsesDefaultValues()
    {
        var row = new EditableRow(new() { ["Id"] = null, ["Name"] = null })
        {
            State = EditState.Inserted
        };
        var tracker = new DataChangeTracker("Customers", ["Id", "Name"], ["Id"]);

        var sql = tracker.BuildStatement(row);

        Assert.Equal("""INSERT INTO "Customers" DEFAULT VALUES;""", sql);
    }

    [Fact]
    public void BuildStatement_ForInsertedRow_WithNoEditedColumns_UsesEmptyColumnListWhenProviderLacksDefaultValues()
    {
        var row = new EditableRow(new() { ["Id"] = null, ["Name"] = null })
        {
            State = EditState.Inserted
        };
        var capabilities = new DatabaseProviderCapabilities("`", "`", "LIMIT") { SupportsInsertDefaultValues = false };
        var tracker = new DataChangeTracker("Customers", ["Id", "Name"], ["Id"], capabilities);

        var sql = tracker.BuildStatement(row);

        Assert.Equal("INSERT INTO `Customers` () VALUES ();", sql);
    }

    [Fact]
    public void BuildStatement_ForModifiedRow_UsesOnlyChangedColumnsAndPrimaryKeyWhere()
    {
        var row = new EditableRow(new()
        {
            ["Id"] = 7,
            ["Name"] = "Original",
            ["Email"] = "old@example.test"
        });
        row.Values["Name"] = "Updated";
        row.State = EditState.Modified;
        var tracker = new DataChangeTracker("Customers", ["Id", "Name", "Email"], ["Id"]);

        var sql = tracker.BuildStatement(row);

        Assert.Equal("""UPDATE "Customers" SET "Name" = 'Updated' WHERE "Id" = 7 AND "Name" = 'Original';""", sql);
    }

    [Fact]
    public void BuildStatement_ForDeletedRow_UsesPrimaryKeyWhereClause()
    {
        var row = new EditableRow(new()
        {
            ["Id"] = 7,
            ["TenantId"] = 3,
            ["Name"] = "Deleted"
        })
        {
            State = EditState.Deleted
        };
        var tracker = new DataChangeTracker("Customers", ["Id", "TenantId", "Name"], ["TenantId", "Id"]);

        var sql = tracker.BuildStatement(row);

        Assert.Equal("""DELETE FROM "Customers" WHERE "TenantId" = 3 AND "Id" = 7;""", sql);
    }

    [Fact]
    public void BuildStatement_EscapesQuotedIdentifiers()
    {
        var row = new EditableRow(new()
        {
            ["Id\"Archive"] = 1,
            ["Customer\"Name"] = "Ada"
        }, editedColumns: ["Id\"Archive", "Customer\"Name"])
        {
            State = EditState.Inserted
        };
        var tracker = new DataChangeTracker("Customer\"Archive", ["Id\"Archive", "Customer\"Name"], ["Id\"Archive"]);

        var sql = tracker.BuildStatement(row);

        Assert.Equal(
            """INSERT INTO "Customer""Archive" ("Id""Archive", "Customer""Name") VALUES (1, 'Ada');""",
            sql);
    }

    [Fact]
    public void GenerateSqlPreview_IncludesOnlyDirtyRows()
    {
        var unchanged = new EditableRow(new() { ["Id"] = 1, ["Name"] = "Same" });
        var inserted = new EditableRow(new() { ["Id"] = 2, ["Name"] = "New" }, editedColumns: ["Id", "Name"])
        {
            State = EditState.Inserted
        };
        var tracker = new DataChangeTracker("Customers", ["Id", "Name"], ["Id"]);

        var sql = tracker.GenerateSqlPreview([unchanged, inserted]);

        Assert.Equal("""INSERT INTO "Customers" ("Id", "Name") VALUES (2, 'New');""", sql);
    }

    [Fact]
    public void BuildCommand_ForInsertedRow_UsesParameters()
    {
        var row = new EditableRow(new()
        {
            ["Id"] = 1,
            ["Name"] = "O'Brien"
        }, editedColumns: ["Id", "Name"])
        {
            State = EditState.Inserted
        };
        var tracker = new DataChangeTracker("Customers", ["Id", "Name"], ["Id"]);

        var command = tracker.BuildCommand(row);

        Assert.NotNull(command);
        Assert.Equal("""INSERT INTO "Customers" ("Id", "Name") VALUES (@p0, @p1);""", command.CommandText);
        Assert.Equal(1, command.Parameters["@p0"]);
        Assert.Equal("O'Brien", command.Parameters["@p1"]);
    }

    [Fact]
    public void BuildCommand_ForModifiedRow_UsesChangedValuesAndPrimaryKeyParameters()
    {
        var row = new EditableRow(new()
        {
            ["Id"] = 7,
            ["Name"] = "Original",
            ["Email"] = "old@example.test"
        });
        row.Values["Name"] = "Updated";
        row.State = EditState.Modified;
        var tracker = new DataChangeTracker("Customers", ["Id", "Name", "Email"], ["Id"]);

        var command = tracker.BuildCommand(row);

        Assert.NotNull(command);
        Assert.Equal("""UPDATE "Customers" SET "Name" = @p0 WHERE "Id" = @p1 AND "Name" = @p2;""", command.CommandText);
        Assert.Equal("Updated", command.Parameters["@p0"]);
        Assert.Equal(7, command.Parameters["@p1"]);
        Assert.Equal("Original", command.Parameters["@p2"]);
    }

    [Fact]
    public void BuildCommand_ForDeletedRow_UsesPrimaryKeyParameters()
    {
        var row = new EditableRow(new()
        {
            ["Id"] = 7,
            ["TenantId"] = 3,
            ["Name"] = "Deleted"
        })
        {
            State = EditState.Deleted
        };
        var tracker = new DataChangeTracker("Customers", ["Id", "TenantId", "Name"], ["TenantId", "Id"]);

        var command = tracker.BuildCommand(row);

        Assert.NotNull(command);
        Assert.Equal("""DELETE FROM "Customers" WHERE "TenantId" = @p0 AND "Id" = @p1;""", command.CommandText);
        Assert.Equal(3, command.Parameters["@p0"]);
        Assert.Equal(7, command.Parameters["@p1"]);
    }

    private static EditableRow InsertedRow(params (string Column, object? Value)[] values) =>
        new(values.ToDictionary(v => v.Column, v => v.Value), editedColumns: values.Select(v => v.Column))
        {
            State = EditState.Inserted
        };

    [Fact]
    public void BuildStatement_ForInsertedRow_IncludesAnExplicitlySetColumn_EvenAsNull()
    {
        var row = InsertedRow(("Id", 42), ("Status", null));
        var tracker = new DataChangeTracker("Customers", ["Id", "Name", "Status"], ["Id"]);

        Assert.Equal("""INSERT INTO "Customers" ("Id", "Status") VALUES (42, NULL);""", tracker.BuildStatement(row));
    }

    [Fact]
    public void BuildStatement_ForInsertedRowWithNoEdits_UsesMySqlsAllDefaultsForm()
    {
        var row = new EditableRow(new() { ["Id"] = null }) { State = EditState.Inserted };
        var tracker = new DataChangeTracker("Customers", ["Id"], ["Id"], new MySqlProvider().Capabilities);

        Assert.Equal("INSERT INTO `Customers` () VALUES ();", tracker.BuildStatement(row));
        Assert.Equal("INSERT INTO `Customers` () VALUES ();", tracker.BuildCommand(row)!.CommandText);
    }

    [Fact]
    public void BuildStatement_RendersBooleansAsTheProvidersLiteral()
    {
        var row = InsertedRow(("Active", true), ("Archived", false));
        string Preview(DatabaseProviderCapabilities capabilities) =>
            new DataChangeTracker("t", ["Active", "Archived"], [], capabilities).BuildStatement(row);

        Assert.EndsWith("VALUES (TRUE, FALSE);", Preview(new PostgreSqlProvider().Capabilities));
        Assert.EndsWith("VALUES (TRUE, FALSE);", Preview(new DuckDbProvider().Capabilities));
        Assert.EndsWith("VALUES (1, 0);", Preview(new SqlServerProvider().Capabilities));
    }

    [Fact]
    public void BuildStatement_KeepsFractionalSecondsOnlyWhenPresent()
    {
        var row = InsertedRow(
            ("Whole", new DateTime(2026, 1, 2, 3, 4, 5)),
            ("Fraction", new DateTime(2026, 1, 2, 3, 4, 5).AddTicks(1_250_000)));
        var tracker = new DataChangeTracker("t", ["Whole", "Fraction"], []);

        Assert.EndsWith("VALUES ('2026-01-02 03:04:05', '2026-01-02 03:04:05.125');", tracker.BuildStatement(row));
    }

    [Fact]
    public void BuildCommand_UsesTheProvidersParameterPlaceholders()
    {
        var row = InsertedRow(("Name", "Ada"));
        var tracker = new DataChangeTracker("t", ["Id", "Name"], ["Id"], new DuckDbProvider().Capabilities);

        var command = tracker.BuildCommand(row)!;

        Assert.Equal("""INSERT INTO "t" ("Name") VALUES (?);""", command.CommandText);
        Assert.Equal(new object?[] { "Ada" }, command.Parameters.Values);
    }

    [Fact]
    public void Update_SkipsABinaryCellSetToAnEqualCopy()
    {
        var row = new EditableRow(new() { ["Id"] = 1, ["Name"] = "Ada", ["Data"] = new byte[] { 1, 2 } })
        {
            State = EditState.Modified
        };
        row.Values["Data"] = new byte[] { 1, 2 };
        var tracker = new DataChangeTracker("Customers", ["Id", "Name", "Data"], ["Id"]);

        Assert.Equal("-- (no changes detected)", tracker.BuildStatement(row));
        Assert.Null(tracker.BuildCommand(row));

        row.Values["Name"] = "Grace";

        Assert.Equal("""UPDATE "Customers" SET "Name" = 'Grace' WHERE "Id" = 1 AND "Name" = 'Ada';""", tracker.BuildStatement(row));
        Assert.Equal("""UPDATE "Customers" SET "Name" = @p0 WHERE "Id" = @p1 AND "Name" = @p2;""", tracker.BuildCommand(row)!.CommandText);
    }

    // ── RAIN-P2-12: concurrency checks ───────────────────────────────────────

    [Fact]
    public void Update_ChecksEachEditedCellStillHoldsItsOriginalValue()
    {
        var row = new EditableRow(new() { ["Id"] = 7, ["Name"] = "Original", ["Email"] = null, ["Notes"] = "same" });
        row.Values["Name"] = "Updated";
        row.Values["Email"] = "new@example.test";
        row.State = EditState.Modified;
        var tracker = new DataChangeTracker("Customers", ["Id", "Name", "Email", "Notes"], ["Id"]);

        var command = tracker.BuildCommand(row)!;

        Assert.Equal(
            """UPDATE "Customers" SET "Name" = @p0, "Email" = @p1 WHERE "Id" = @p2 AND "Name" = @p3 AND "Email" IS NULL;""",
            command.CommandText);
        Assert.Equal(new object?[] { "Updated", "new@example.test", 7, "Original" }, command.Parameters.Values);
        Assert.Equal(1, command.ExpectedRecordsAffected);
        Assert.Equal(
            """UPDATE "Customers" SET "Name" = 'Updated', "Email" = 'new@example.test' WHERE "Id" = 7 AND "Name" = 'Original' AND "Email" IS NULL;""",
            tracker.BuildStatement(row));
    }

    [Fact]
    public void Update_OfAKeyColumn_ChecksItOnlyOnce()
    {
        var row = new EditableRow(new() { ["Id"] = 7, ["Name"] = "Ada" });
        row.Values["Id"] = 8;
        row.State = EditState.Modified;
        var tracker = new DataChangeTracker("Customers", ["Id", "Name"], ["Id"]);

        Assert.Equal("""UPDATE "Customers" SET "Id" = @p0 WHERE "Id" = @p1;""", tracker.BuildCommand(row)!.CommandText);
    }

    [Fact]
    public void Update_UnderAllColumnIdentity_ChecksEachColumnOnlyOnce()
    {
        var row = new EditableRow(new() { ["Name"] = "Ada", ["Email"] = "a@example.test" });
        row.Values["Name"] = "Grace";
        row.State = EditState.Modified;
        var tracker = new DataChangeTracker("Customers", ["Name", "Email"]);

        Assert.Equal("""UPDATE "Customers" SET "Name" = 'Grace' WHERE "Name" = 'Ada' AND "Email" = 'a@example.test';""", tracker.BuildStatement(row));
    }

    [Fact]
    public void Update_SkipsTheCheckOnColumnsTheDatabaseCannotCompare()
    {
        var row = new EditableRow(new() { ["Id"] = 7, ["Name"] = "Ada", ["Payload"] = "{}" });
        row.Values["Name"] = "Grace";
        row.Values["Payload"] = "{\"a\":1}";
        row.State = EditState.Modified;
        var tracker = new DataChangeTracker("Customers", ["Id", "Name", "Payload"], ["Id"], nonComparableColumns: ["Payload"]);

        Assert.Equal(
            """UPDATE "Customers" SET "Name" = @p0, "Payload" = @p1 WHERE "Id" = @p2 AND "Name" = @p3;""",
            tracker.BuildCommand(row)!.CommandText);
    }

    [Fact]
    public void Update_ThatOverwritesConcurrentChanges_IdentifiesTheRowByKeyAlone()
    {
        var row = new EditableRow(new() { ["Id"] = 7, ["Name"] = "Ada" });
        row.Values["Name"] = "Grace";
        row.State = EditState.Modified;
        row.OverwritesConcurrentChanges = true;
        var tracker = new DataChangeTracker("Customers", ["Id", "Name"], ["Id"]);

        Assert.Equal("""UPDATE "Customers" SET "Name" = @p0 WHERE "Id" = @p1;""", tracker.BuildCommand(row)!.CommandText);
        Assert.Equal("""UPDATE "Customers" SET "Name" = 'Grace' WHERE "Id" = 7;""", tracker.BuildStatement(row));
    }

    [Fact]
    public void Revert_ClearsTheOverwriteChoice()
    {
        var row = new EditableRow(new() { ["Id"] = 7, ["Name"] = "Ada" }) { State = EditState.Modified, OverwritesConcurrentChanges = true };

        row.Revert();

        Assert.False(row.OverwritesConcurrentChanges);
    }

    [Fact]
    public void BuildRowQuery_SelectsTheRowByItsOriginalKey()
    {
        var row = new EditableRow(new() { ["Id"] = 7, ["Region"] = null, ["Name"] = "Ada" });
        row.Values["Id"] = 8;
        row.State = EditState.Modified;
        var tracker = new DataChangeTracker("Customers", ["Id", "Region", "Name"], ["Id", "Region"]);

        var query = tracker.BuildRowQuery(row);

        Assert.Equal("""SELECT * FROM "Customers" WHERE "Id" = @p0 AND "Region" IS NULL;""", query.Sql);
        Assert.Equal(new object?[] { 7 }, query.Parameters);
    }

    [Theory]
    [InlineData("character(5)")]
    [InlineData("bpchar")]
    [InlineData("json")]
    public void PostgreSql_CannotCompareBlankPaddedCharOrJson(string typeName)
    {
        var capabilities = new DatabaseService().GetProvider("PostgreSQL").Capabilities;

        Assert.False(capabilities.CanCompareType(typeName));
    }

    [Theory]
    [InlineData("character varying(5)")]
    [InlineData("text")]
    [InlineData("jsonb")]
    [InlineData("numeric(18,4)")]
    public void PostgreSql_CanCompareOtherTypes(string typeName)
    {
        var capabilities = new DatabaseService().GetProvider("PostgreSQL").Capabilities;

        Assert.True(capabilities.CanCompareType(typeName));
    }
}
