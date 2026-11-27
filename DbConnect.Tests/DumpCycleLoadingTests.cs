using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

/// <summary>Ordering and loading of foreign-key cycles in a dump (sql-dump-and-insert-export/06).</summary>
public sealed class DumpCycleLoadingTests
{
    private static TableDefinitionSnapshot Snapshot(string name, params TableDefinitionPart[] parts) =>
        new(new DatabaseObjectIdentity(null, "public", name, SchemaNodeType.Table), "PostgreSQL",
            [new(TableDefinitionPartKind.Table, name, $"CREATE TABLE public.{name} (id integer)"), .. parts]);

    private static ForeignKeyRelationship Key(string name, string table, string referenced) =>
        new(name, "public." + table, "public." + referenced, ["ref"], ["id"]);

    private static string Names(IEnumerable<TableDefinitionSnapshot> tables) => string.Join(" ", tables.Select(t => t.Table.Name));

    [Fact]
    public void CyclesStayTogetherAfterTheirParentsAndBeforeTheirChildren()
    {
        var tables = new[] { Snapshot("d"), Snapshot("a"), Snapshot("b"), Snapshot("c"), Snapshot("e"), Snapshot("p") };
        var order = DumpTableOrder.Sort(tables, [
            Key("d_b", "d", "b"), Key("a_b", "a", "b"), Key("b_a", "b", "a"), Key("b_p", "b", "p"), Key("c_c", "c", "c")
        ]);

        // The earliest table in catalog order whose parents are placed comes next; a cycle counts as its first table.
        Assert.Equal("c e p a b d", Names(order.Tables));
        Assert.Collection(order.Cycles,
            cycle =>
            {
                Assert.Equal("c", Names(cycle.Tables));
                Assert.True(cycle.IsSelfReference);
            },
            cycle =>
            {
                Assert.Equal("a b", Names(cycle.Tables));
                Assert.Equal(["a_b", "b_a"], cycle.Keys.Select(k => k.Key.Name).Order());
                Assert.False(cycle.IsSelfReference);
            });
    }

    [Fact]
    public void ALongForeignKeyChainSortsWithoutRecursion()
    {
        var tables = Enumerable.Range(0, 3_000).Select(i => Snapshot("t" + i)).ToArray();
        var keys = Enumerable.Range(1, tables.Length - 1).Select(i => Key("k" + i, "t" + (i - 1), "t" + i)).ToList();

        var order = DumpTableOrder.Sort(tables, keys);

        Assert.Equal("t2999", order.Tables[0].Table.Name);
        Assert.Equal("t0", order.Tables[^1].Table.Name);
        Assert.Empty(order.Cycles);
    }

    [Theory]
    [InlineData(DumpMode.StructureAndData, "Foreign keys in these cycles: added after the data")]
    [InlineData(DumpMode.StructureOnly, "Foreign keys in these cycles: added after all tables are created")]
    public void SeparateForeignKeysOfACycleAreHeldBack(DumpMode mode, string line)
    {
        var aKey = new TableDefinitionPart(TableDefinitionPartKind.Constraint, "a_b", "ALTER TABLE public.a ADD CONSTRAINT a_b FOREIGN KEY …");
        var aComment = new TableDefinitionPart(TableDefinitionPartKind.Comment, "a_b", "COMMENT ON CONSTRAINT a_b ON public.a IS 'x'");
        var aCheck = new TableDefinitionPart(TableDefinitionPartKind.Constraint, "a_check", "ALTER TABLE public.a ADD CONSTRAINT a_check CHECK …");
        var aOut = new TableDefinitionPart(TableDefinitionPartKind.Constraint, "a_p", "ALTER TABLE public.a ADD CONSTRAINT a_p FOREIGN KEY …");
        var bKey = new TableDefinitionPart(TableDefinitionPartKind.Constraint, "b_a", "ALTER TABLE public.b ADD CONSTRAINT b_a FOREIGN KEY …");
        var a = Snapshot("a", aKey, aComment, aCheck, aOut);
        var b = Snapshot("b", bKey);
        var p = Snapshot("p");
        var order = DumpTableOrder.Sort([a, b, p], [Key("a_b", "a", "b"), Key("b_a", "b", "a"), Key("a_p", "a", "p")]);

        var plan = DumpCycleLoading.Plan(order, ProviderCapabilityDeclarations.PostgreSql, "PostgreSQL", mode);

        Assert.True(plan.IsDeferred(a, aKey));
        Assert.True(plan.IsDeferred(a, aComment));
        Assert.True(plan.IsDeferred(b, bKey));
        Assert.False(plan.IsDeferred(a, aCheck));
        Assert.False(plan.IsDeferred(a, aOut), "a key to a table outside the cycle is created with its table");
        Assert.False(plan.HasDeferred(p));
        Assert.Null(plan.ChecksOff);
        Assert.Equal(["Foreign-key cycles:", "  public.a, public.b", line], plan.HeaderLines("PostgreSQL"));
    }

    [Fact]
    public void InlineForeignKeysOfACycleTurnChecksOffWhereTheEngineCan()
    {
        var order = DumpTableOrder.Sort([Snapshot("a"), Snapshot("b")], [Key("1", "a", "b"), Key("2", "b", "a")]);

        var plan = DumpCycleLoading.Plan(order, ProviderCapabilityDeclarations.MySql, "MySQL", DumpMode.StructureAndData);

        Assert.Same(ProviderCapabilityDeclarations.MySql.ForeignKeyChecks, plan.ChecksOff);
        Assert.Empty(plan.OrderChecked);
        Assert.Contains("Foreign-key checks: off while this script loads (SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS, FOREIGN_KEY_CHECKS=0;), " +
                        "restored at its end (SET FOREIGN_KEY_CHECKS=@OLD_FOREIGN_KEY_CHECKS;)", plan.HeaderLines("MySQL"));
    }

    [Fact]
    public void ACycleOfSeveralTablesFailsWhereChecksCannotBeTurnedOff()
    {
        var order = DumpTableOrder.Sort([Snapshot("b"), Snapshot("a")], [Key("1", "a", "b"), Key("2", "b", "a")]);

        var error = Assert.Throws<DatabaseDumpException>(() =>
            DumpCycleLoading.Plan(order, ProviderCapabilityDeclarations.DuckDb, "DuckDB", DumpMode.StructureOnly));

        Assert.Equal("Tables public.b, public.a reference each other through foreign keys, and DuckDB can neither turn off " +
                     "foreign-key checks nor add foreign keys after the data, so the dump would not load.", error.Message);
    }

    [Fact]
    public void ASelfReferenceIsOrderCheckedWhereChecksCannotBeTurnedOff()
    {
        var c = Snapshot("c");
        var order = DumpTableOrder.Sort([c], [Key("1", "c", "c")]);

        var plan = DumpCycleLoading.Plan(order, ProviderCapabilityDeclarations.DuckDb, "DuckDB", DumpMode.StructureAndData);

        var (table, key) = Assert.Single(plan.OrderChecked);
        Assert.Same(c, table);
        Assert.Equal("1", key.Name);
        Assert.Null(plan.ChecksOff);
    }
}
