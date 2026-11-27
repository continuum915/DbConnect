using DbConnect.Models;
using DbConnect.Services;

using static DbConnect.Tests.ProviderConformance.ScratchComparisons;

namespace DbConnect.Tests.ProviderConformance;

/// <summary>
/// Same-provider data transfer on PostgreSQL, MySQL and SQL Server (data-transfer/01, 02): tables with a foreign key,
/// identity and serial columns copy into an empty database in multi-row batches, keep their identity values and go on
/// generating the same next values; per batch with continue-on-error, PostgreSQL's aborted transactions and the
/// others' rejected rows skip only the colliding row. Skipped unless the container lane's
/// DBCONNECT_CONFORMANCE_*_CONNECTION_STRING variables are set (build/databases/run-provider-integration.ps1).
/// </summary>
public sealed class DataTransferServerTests
{
    private static readonly string[] Tables = ["z_customers", "m_orders", "k_events"];

    private static string Fixture(string provider) => provider switch
    {
        "PostgreSQL" => """
            CREATE TABLE z_customers (id integer PRIMARY KEY, name varchar(50) NOT NULL UNIQUE, balance numeric(10,2), payload jsonb,
                data bytea, seen timestamptz);
            CREATE TABLE m_orders (id integer PRIMARY KEY, customer_id integer NOT NULL REFERENCES z_customers(id), placed timestamp);
            CREATE TABLE k_events (id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY, s serial, what text);
            INSERT INTO z_customers VALUES (1, 'O''Brien', 12.50, '{"a": [1, "x"]}', '\xDEADBEEF', '2026-01-02 03:04:05.123456+02'),
                (2, 'héllo 日本語', NULL, NULL, NULL, NULL), (3, 'Cy', -1, '[]', '\x', '1999-12-31 23:59:59+00');
            INSERT INTO m_orders VALUES (10, 1, '2026-01-31 12:00:00.5'), (11, 2, NULL), (12, 2, '2000-01-01');
            INSERT INTO k_events (what) VALUES ('one'), ('two'), ('three');
            DELETE FROM k_events WHERE what = 'two';
            """,
        "MySQL" => """
            CREATE TABLE z_customers (id int PRIMARY KEY, name varchar(50) NOT NULL UNIQUE, balance decimal(10,2), payload json,
                data varbinary(16), seen datetime(6)) ENGINE=InnoDB;
            CREATE TABLE m_orders (id int PRIMARY KEY, customer_id int NOT NULL, placed datetime(3),
                CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES z_customers(id)) ENGINE=InnoDB;
            CREATE TABLE k_events (id int AUTO_INCREMENT PRIMARY KEY, what varchar(20)) ENGINE=InnoDB;
            INSERT INTO z_customers VALUES (1, 'O''Brien', 12.50, '{"a": [1, "x"]}', 0xDEADBEEF, '2026-01-02 03:04:05.123456'),
                (2, 'héllo 日本語', NULL, NULL, NULL, NULL), (3, 'Cy', -1, '[]', X'', '1999-12-31 23:59:59');
            INSERT INTO m_orders VALUES (10, 1, '2026-01-31 12:00:00.5'), (11, 2, NULL), (12, 2, '2000-01-01');
            INSERT INTO k_events (what) VALUES ('one'), ('two'), ('three');
            DELETE FROM k_events WHERE what = 'two';
            """,
        _ => """
            CREATE TABLE z_customers (id int PRIMARY KEY, name nvarchar(50) NOT NULL UNIQUE, balance decimal(10,2), data varbinary(16),
                seen datetimeoffset(7), uid uniqueidentifier);
            CREATE TABLE m_orders (id int PRIMARY KEY, customer_id int NOT NULL REFERENCES z_customers(id), placed datetime2(3));
            CREATE TABLE k_events (id int IDENTITY(1,1) PRIMARY KEY, what nvarchar(20), rv rowversion);
            INSERT INTO z_customers VALUES (1, N'O''Brien', 12.50, 0xDEADBEEF, '2026-01-02 03:04:05.1234567 +02:00', 'F3A9C1E2-0B1D-4C5E-9F00-123456789ABC'),
                (2, N'héllo 日本語', NULL, NULL, NULL, NULL), (3, N'Cy', -1, 0x, '1999-12-31 23:59:59 +00:00', NULL);
            INSERT INTO m_orders VALUES (10, 1, '2026-01-31 12:00:00.5'), (11, 2, NULL), (12, 2, '2000-01-01');
            INSERT INTO k_events (what) VALUES ('one'), ('two'), ('three');
            DELETE FROM k_events WHERE what = 'two';
            """
    };

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("PostgreSQL")]
    [InlineData("MySQL")]
    [InlineData("SQL Server")]
    public async Task TablesWithIdentityColumnsCopyIntoAnEmptyDatabase(string provider)
    {
        await using var server = ScratchServer.For(provider);
        if (server == null) return;
        var source = await server.CreateDatabaseAsync("src", Fixture(provider));
        var target = await server.CreateDatabaseAsync("dst", null);

        var outcomes = await DataTransferJob.RunAsync(source, target, Tables.Select(t => Identity(source, t)).ToList(),
            new DataTransferOptions { BatchSize = 2 });

        Assert.All(outcomes, o => Assert.True(o.Status == DataTransferTableStatus.Created, $"{o.Table.Name}: {o.Status} {o.Diagnostic}"));
        Assert.Equal([3L, 3L, 2L], outcomes.Select(o => o.RowsCopied));
        foreach (var table in Tables)
        {
            Assert.Equal(await StatementsAsync(source, table), await StatementsAsync(target, table));
            var compared = table == "k_events" && provider == "SQL Server" ? "id, what" : "*"; // rowversion is written by each engine
            Assert.Equal(await RowsAsync(source, table, compared), await RowsAsync(target, table, compared));
        }
        // Identity and serial values arrived as they were, and both databases go on generating the same next values.
        foreach (var session in new[] { source, target })
            await server.RunScriptAsync(session, "INSERT INTO k_events (what) VALUES ('next')");
        var events = provider == "SQL Server" ? "id, what" : "*";
        Assert.Equal(await RowsAsync(source, "k_events", events), await RowsAsync(target, "k_events", events));
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("PostgreSQL")]
    [InlineData("MySQL")]
    [InlineData("SQL Server")]
    public async Task PerBatchContinueOnErrorSkipsOnlyTheCollidingRow(string provider)
    {
        await using var server = ScratchServer.For(provider);
        if (server == null) return;
        var source = await server.CreateDatabaseAsync("src", Fixture(provider));
        var target = await server.CreateDatabaseAsync("dst", provider == "MySQL"
            ? "CREATE TABLE k_events (id int AUTO_INCREMENT PRIMARY KEY, what varchar(20)) ENGINE=InnoDB; INSERT INTO k_events VALUES (3, 'kept');"
            : provider == "PostgreSQL"
                ? "CREATE TABLE k_events (id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY, s serial, what text); INSERT INTO k_events OVERRIDING SYSTEM VALUE VALUES (3, 99, 'kept');"
                : "CREATE TABLE k_events (id int IDENTITY(1,1) PRIMARY KEY, what nvarchar(20), rv rowversion); SET IDENTITY_INSERT k_events ON; INSERT INTO k_events (id, what) VALUES (3, 'kept'); SET IDENTITY_INSERT k_events OFF;");

        var outcome = Assert.Single(await DataTransferJob.RunAsync(source, target, [Identity(source, "k_events")], new DataTransferOptions
        {
            BatchSize = 2,
            Transaction = DataTransferTransactionMode.PerBatch,
            ContinueOnError = true
        }));

        Assert.True(outcome.Status == DataTransferTableStatus.Appended, outcome.Diagnostic);
        Assert.Equal(1, outcome.RowsCopied);
        var skipped = Assert.Single(outcome.Skipped);
        Assert.Equal(2, skipped.RowNumber);
        Assert.Equal("id=3", skipped.Key);
        Assert.Equal("Int32:1|String:one\nInt32:3|String:kept", await RowsAsync(target, "k_events", "id, what"));
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("PostgreSQL")]
    [InlineData("MySQL")]
    [InlineData("SQL Server")]
    public async Task DropAndRecreateAndEmptyReplaceAnExistingTable(string provider)
    {
        await using var server = ScratchServer.For(provider);
        if (server == null) return;
        var source = await server.CreateDatabaseAsync("src", Fixture(provider));
        // Older versions of both tables, with rows of their own; m_orders keeps its foreign key to z_customers.
        var target = await server.CreateDatabaseAsync("dst", null);
        await DataTransferJob.RunAsync(source, target, [Identity(source, "z_customers"), Identity(source, "m_orders")], new DataTransferOptions());
        await server.RunScriptAsync(target, "DELETE FROM m_orders WHERE id = 12");
        await server.RunScriptAsync(target, "INSERT INTO m_orders (id, customer_id) VALUES (99, 3)");
        await server.RunScriptAsync(target, "ALTER TABLE z_customers ADD legacy int");

        var outcomes = await DataTransferJob.RunAsync(source, target, [Identity(source, "m_orders"), Identity(source, "z_customers")],
            new DataTransferOptions
            {
                TableModes = new Dictionary<DatabaseObjectIdentity, DataTransferTargetMode>
                {
                    [Identity(source, "m_orders")] = DataTransferTargetMode.Empty,
                    [Identity(source, "z_customers")] = DataTransferTargetMode.DropAndRecreate
                }
            });

        // m_orders is emptied and refilled. z_customers cannot be dropped while m_orders references it, and says so; MySQL,
        // whose foreign-key checks are off for the transfer, drops it and creates it again under the same name.
        Assert.True(outcomes[0].Status == DataTransferTableStatus.Emptied, outcomes[0].Diagnostic);
        Assert.Equal(await RowsAsync(source, "m_orders"), await RowsAsync(target, "m_orders"));
        if (provider == "MySQL")
        {
            Assert.True(outcomes[1].Status == DataTransferTableStatus.Recreated, outcomes[1].Diagnostic);
            return;
        }
        Assert.Equal(DataTransferTableStatus.Failed, outcomes[1].Status);
        Assert.Contains("The existing table could not be dropped", outcomes[1].Diagnostic);
        Assert.NotEqual(await StatementsAsync(source, "z_customers"), await StatementsAsync(target, "z_customers"));

        // Without the reference it is dropped and created again from the source's definition.
        await server.RunScriptAsync(target, "DROP TABLE m_orders");
        var recreated = Assert.Single(await DataTransferJob.RunAsync(source, target, [Identity(source, "z_customers")],
            new DataTransferOptions { Mode = DataTransferTargetMode.DropAndRecreate }));
        Assert.True(recreated.Status == DataTransferTableStatus.Recreated, recreated.Diagnostic);
        Assert.Equal(await StatementsAsync(source, "z_customers"), await StatementsAsync(target, "z_customers"));
        Assert.Equal(await RowsAsync(source, "z_customers"), await RowsAsync(target, "z_customers"));
    }

    // a_people and b_teams reference each other, and d_tasks references b_teams; the transfer leaves d_tasks' parent out.
    private static string CycleFixture(string provider) => provider switch
    {
        "MySQL" => """
            SET FOREIGN_KEY_CHECKS=0;
            CREATE TABLE b_teams (id int PRIMARY KEY, captain_id int, CONSTRAINT fk_captain FOREIGN KEY (captain_id) REFERENCES a_people(id)) ENGINE=InnoDB;
            CREATE TABLE a_people (id int PRIMARY KEY, team_id int, CONSTRAINT fk_team FOREIGN KEY (team_id) REFERENCES b_teams(id)) ENGINE=InnoDB;
            CREATE TABLE d_tasks (id int PRIMARY KEY, team_id int, CONSTRAINT fk_task_team FOREIGN KEY (team_id) REFERENCES b_teams(id)) ENGINE=InnoDB;
            INSERT INTO b_teams VALUES (1, 2), (2, NULL);
            INSERT INTO a_people VALUES (1, 2), (2, 1);
            INSERT INTO d_tasks VALUES (1, 1);
            SET FOREIGN_KEY_CHECKS=1;
            """,
        _ => """
            CREATE TABLE b_teams (id int PRIMARY KEY, captain_id int);
            CREATE TABLE a_people (id int PRIMARY KEY, team_id int REFERENCES b_teams(id));
            ALTER TABLE b_teams ADD CONSTRAINT fk_captain FOREIGN KEY (captain_id) REFERENCES a_people(id);
            CREATE TABLE d_tasks (id int PRIMARY KEY, team_id int REFERENCES b_teams(id));
            INSERT INTO b_teams VALUES (1, NULL), (2, NULL);
            INSERT INTO a_people VALUES (1, 2), (2, 1);
            UPDATE b_teams SET captain_id = 2 WHERE id = 1;
            INSERT INTO d_tasks VALUES (1, 1);
            """
    };

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("PostgreSQL")]
    [InlineData("MySQL")]
    [InlineData("SQL Server")]
    public async Task ACycleLoadsWithItsKeysAddedAfterTheData(string provider)
    {
        await using var server = ScratchServer.For(provider);
        if (server == null) return;
        var source = await server.CreateDatabaseAsync("src", CycleFixture(provider));
        var target = await server.CreateDatabaseAsync("dst", null);

        var outcomes = await DataTransferJob.RunAsync(source, target, [Identity(source, "a_people"), Identity(source, "b_teams")],
            new DataTransferOptions { BatchSize = 1 });

        Assert.All(outcomes, o => Assert.True(o.Status == DataTransferTableStatus.Created, $"{o.Table.Name}: {o.Diagnostic}"));
        Assert.All(outcomes, o => Assert.DoesNotContain(o.Notes, n => n.Contains("Foreign key")));
        foreach (var table in new[] { "a_people", "b_teams" })
        {
            Assert.Equal(await StatementsAsync(source, table), await StatementsAsync(target, table));
            Assert.Equal(await RowsAsync(source, table), await RowsAsync(target, table));
        }

        // d_tasks' parent stays behind: a separate key is reported and not added; an inline one comes with the table.
        var tasks = Assert.Single(await DataTransferJob.RunAsync(source, await server.CreateDatabaseAsync("dst2", null),
            [Identity(source, "d_tasks")], new DataTransferOptions()));
        Assert.True(tasks.Status == DataTransferTableStatus.Created, tasks.Diagnostic);
        Assert.Contains(tasks.Notes, n => n.Contains("which is not part of the transfer"));
        if (provider != "MySQL") Assert.Contains(tasks.Notes, n => n.Contains("was not created"));

        // Foreign keys turned off leave the separate ones out.
        if (provider == "MySQL") return;
        var bare = await server.CreateDatabaseAsync("dst3", null);
        var noKeys = await DataTransferJob.RunAsync(source, bare, [Identity(source, "a_people"), Identity(source, "b_teams")],
            new DataTransferOptions { IncludeForeignKeys = false });
        Assert.All(noKeys, o => Assert.Contains(o.Notes, n => n.Contains("foreign keys were turned off")));
        Assert.DoesNotContain("FOREIGN KEY", await StatementsAsync(bare, "a_people") + await StatementsAsync(bare, "b_teams"),
            StringComparison.OrdinalIgnoreCase);
    }
}
