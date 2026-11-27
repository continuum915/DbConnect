using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using Xunit;
using Xunit.Sdk;

namespace DbConnect.Tests.ProviderConformance;

/// <summary>Provider-wide expectations shared by every conformance lane.</summary>
public static class ProviderConformanceContract
{
    public static IReadOnlyList<ProviderConformanceDescriptor> Descriptors { get; } =
    [
        new("SQLite", true, true, "@p{n}", "DBCONNECT_CONFORMANCE_SQLITE_CONNECTION_STRING"),
        new("PostgreSQL", false, true, "@p{n}", "DBCONNECT_CONFORMANCE_POSTGRESQL_CONNECTION_STRING"),
        new("SQL Server", false, true, "@p{n}", "DBCONNECT_CONFORMANCE_SQLSERVER_CONNECTION_STRING"),
        new("MySQL", false, true, "@p{n}", "DBCONNECT_CONFORMANCE_MYSQL_CONNECTION_STRING"),
        new("DuckDB", true, true, "?", "DBCONNECT_CONFORMANCE_DUCKDB_CONNECTION_STRING")
    ];

    /// <summary>
    /// Validates the provider declaration without requiring a live server. This
    /// runs for all five built-in providers on every test invocation.
    /// </summary>
    public static void AssertDeclaration(IDatabaseProvider provider)
    {
        var descriptor = Descriptors.SingleOrDefault(d =>
            string.Equals(d.ProviderName, provider.ProviderName, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null)
            throw new XunitException($"{provider.ProviderName} [registry]: no conformance descriptor is registered.");

        Require(provider, "dialect", "identifier quotes are non-empty.",
            !string.IsNullOrEmpty(provider.Capabilities.IdentifierOpenQuote) &&
            !string.IsNullOrEmpty(provider.Capabilities.IdentifierCloseQuote));
        Require(provider, "dialect", "limit style is declared.", !string.IsNullOrWhiteSpace(provider.Capabilities.LimitStyle));
        Require(provider, "dialect", "parameter prefix is declared.", !string.IsNullOrWhiteSpace(provider.Capabilities.ParameterPrefix));
        Require(provider, "explain", "an explain prefix is declared.", !string.IsNullOrWhiteSpace(provider.ExplainPrefix));

        var capabilities = provider.Capabilities;
        Require(provider, "transaction", "transaction flags agree with TransactionSupport.",
            capabilities.SupportsTransactionalBatches == (capabilities.TransactionSupport != TransactionSupportLevel.None) &&
            capabilities.GuaranteesTransactionalBatches == (capabilities.TransactionSupport == TransactionSupportLevel.Guaranteed));

        foreach (var type in new[]
        {
            SchemaNodeType.View,
            SchemaNodeType.Index,
            SchemaNodeType.Trigger,
            SchemaNodeType.Function,
            SchemaNodeType.Procedure
        })
        {
            var expected = type switch
            {
                SchemaNodeType.View => capabilities.SupportsViews,
                SchemaNodeType.Index => capabilities.SupportsIndexes,
                SchemaNodeType.Trigger => capabilities.SupportsTriggers,
                SchemaNodeType.Function => capabilities.SupportsFunctions,
                SchemaNodeType.Procedure => capabilities.SupportsProcedures,
                _ => true
            };
            Require(provider, "capability declaration", $"SupportsObjectType({type}) matches its flag.",
                capabilities.SupportsObjectType(type) == expected);
        }

        var escaped = capabilities.QuoteIdentifier("rain]conformance\"name");
        Require(provider, "identifier quoting", "quoted identifiers include both delimiters.",
            escaped.StartsWith(capabilities.IdentifierOpenQuote, StringComparison.Ordinal) &&
            escaped.EndsWith(capabilities.IdentifierCloseQuote, StringComparison.Ordinal));

        var normalized = provider.NormalizeExplainResult(new QueryResult
        {
            ColumnInfos = [new ColumnInfo { Name = "plan", Ordinal = 0 }],
            ObjectRows = [new object?[] { "synthetic plan" }]
        });
        Require(provider, "explain normalization", "normalization returns a result.", normalized is not null);
        if (normalized is null)
            return;
        Require(provider, "explain normalization", "normalization preserves the row count and exposes columns.",
            normalized.ColumnInfos.Count > 0 && normalized.ObjectRows.Count == 1);

        // ExecuteQueryMultipleAsync has no interface default any more, so every provider must supply
        // its own implementation. Whether that implementation genuinely drains multiple result sets
        // (rather than just wrapping a single execution) is verified behaviorally, in RunAsync's
        // "multiple results" check — there is nothing left to check from the declaration alone.
    }

    /// <summary>
    /// Executes the live contract against a seeded fixture. External fixtures
    /// use the exact same method from future container-backed CI lanes.
    /// </summary>
    public static async Task RunAsync(ProviderConformanceFixture fixture)
    {
        var provider = fixture.Provider;
        AssertDeclaration(provider);

        await CheckAsync(fixture, "connection", "TestConnectionAsync", async () =>
        {
            var test = await provider.TestConnectionAsync(fixture.ConnectionString);
            Require(fixture, "connection", $"the test succeeded ({test.CategoryLabel}: {test.Summary} {test.Detail}).", test.Success);
            Require(fixture, "connection", "the test reported a server version.", !string.IsNullOrWhiteSpace(test.ServerVersion));
            Require(fixture, "connection", "the test reported its elapsed time.", test.Elapsed > TimeSpan.Zero);
        });

        await ConnectionFailureConformance.RunAsync(fixture);

        await CheckAsync(fixture, "server version", "GetServerVersionAsync", () =>
        {
            Require(fixture, "server version", "a usable version was captured.",
                !string.IsNullOrWhiteSpace(fixture.DatabaseVersion) &&
                !fixture.DatabaseVersion.Contains("unknown", StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        });

        await CheckAsync(fixture, "schema", "GetSchemaAsync/GetObjectsAsync", async () =>
        {
            var table = await fixture.FindObjectAsync(SchemaNodeType.Table, fixture.TableName);
            var view = await fixture.FindObjectAsync(SchemaNodeType.View, fixture.ViewName);
            Require(fixture, "schema", "the seeded table is enumerable.", table is not null);
            Require(fixture, "schema", "the seeded view is enumerable.", view is not null);

            if (provider.Capabilities.SupportsSchemas && fixture.IsLocal)
            {
                var schemas = await provider.GetSchemasAsync(fixture.ConnectionString);
                Require(fixture, "schema", "the declared schema capability returns a schema.", schemas.Count > 0);
            }
        });

        await CheckAsync(fixture, "indexes", "GetIndexesAsync", async () =>
        {
            if (!provider.Capabilities.SupportsIndexes)
                return;

            var indexes = await provider.GetIndexesAsync(fixture.ConnectionString, fixture.TableName);
            Require(fixture, "indexes", "the seeded index is enumerable.",
                indexes.Any(index => string.Equals(index.Name, fixture.IndexName, StringComparison.OrdinalIgnoreCase)));
        });

        await CheckAsync(fixture, "triggers", "GetTriggerDefinitionAsync", async () =>
        {
            if (provider.ProviderName != "SQLite" || !provider.Capabilities.SupportsTriggers)
                return;

            var trigger = await fixture.FindObjectAsync(SchemaNodeType.Trigger, fixture.TriggerName);
            var definition = await provider.GetTriggerDefinitionAsync(fixture.ConnectionString, "main", fixture.TriggerName);
            var directTriggers = await provider.GetObjectsAsync(fixture.ConnectionString, fixture.TableName, SchemaNodeType.Trigger);
            Require(fixture, "triggers", $"the seeded trigger is enumerable (direct: {string.Join(",", directTriggers.Select(node => node.RawName ?? node.Name))}).", trigger is not null);
            Require(fixture, "triggers", "the seeded trigger definition is available.",
                !string.IsNullOrWhiteSpace(definition) && definition.Contains(fixture.TriggerName, StringComparison.OrdinalIgnoreCase));
        });

        await CheckAsync(fixture, "functions", "GetFunctionDefinitionAsync", async () =>
        {
            if (provider.ProviderName != "DuckDB" || !provider.Capabilities.SupportsFunctions)
                return;

            var function = await fixture.FindObjectAsync(SchemaNodeType.Function, fixture.MacroName);
            var definition = await provider.GetFunctionDefinitionAsync(fixture.ConnectionString, "main", fixture.MacroName);
            Require(fixture, "functions", "the seeded macro is enumerable.", function is not null);
            Require(fixture, "functions", "the seeded macro definition is available.",
                !string.IsNullOrWhiteSpace(definition));
        });

        await CheckAsync(fixture, "query", "ExecuteQueryAsync", async () =>
        {
            var query = $"SELECT {fixture.Quote("id")}, {fixture.Quote("name")} FROM {fixture.QualifiedTableName} WHERE {fixture.Quote("id")} = 1;";
            var result = await provider.ExecuteQueryAsync(fixture.ConnectionString, query);
            Require(fixture, "query", "the seeded row can be read.", !result.HasError && result.ObjectRows.Count == 1);
        });

        await CheckAsync(fixture, "columnar result model", "duplicate column names by ordinal", async () =>
        {
            var id = fixture.Quote("id");
            var query =
                $"SELECT a.{id}, b.{id} FROM {fixture.QualifiedTableName} a, {fixture.QualifiedTableName} b WHERE a.{id} = 1 AND b.{id} = 1;";
            var result = await provider.ExecuteQueryAsync(fixture.ConnectionString, query);
            Require(fixture, "columnar result model", $"the query runs ({result.Error?.Summary}).", !result.HasError);
            Require(fixture, "columnar result model",
                $"two distinct columns named \"id\" appear in the new shape, told apart by ordinal (got {result.ColumnInfos.Count}).",
                result.ColumnInfos.Count == 2 &&
                result.ColumnInfos[0].Name == "id" && result.ColumnInfos[0].Ordinal == 0 &&
                result.ColumnInfos[1].Name == "id" && result.ColumnInfos[1].Ordinal == 1);
            Require(fixture, "columnar result model",
                $"the object-array row carries both values (count: {result.ObjectRows.Count}).",
                result.ObjectRows.Count == 1 && result.ObjectRows[0].Length == 2);
        });

        await CheckAsync(fixture, "result column sources", "ColumnInfo.SourceTable/SourceColumn", async () =>
        {
            // Editable query results rest on this metadata: a plain column must name its table
            // and column, and an expression must name neither, so it can never look editable.
            if (!provider.Capabilities.ReportsResultColumnSources)
                return;

            var id = fixture.Quote("id");
            var name = fixture.Quote("name");
            var amount = fixture.Quote("amount");
            var otherTableName = $"rain_conformance_tags_{Guid.NewGuid():N}";
            var otherTable = fixture.Quote(otherTableName);
            var tag = fixture.Quote("tag");

            async Task<IReadOnlyList<ColumnInfo>> ColumnsAsync(string label, string sql)
            {
                var result = await provider.ExecuteQueryAsync(fixture.ConnectionString, sql);
                Require(fixture, "result column sources", $"{label}: the query runs ({result.Error?.Summary}).", !result.HasError);
                return result.ColumnInfos;
            }

            void RequireSource(string label, ColumnInfo column, string table, string sourceColumn) =>
                Require(fixture, "result column sources",
                    $"{label}: \"{column.Name}\" reports {table}.{sourceColumn} (got {column.SourceTable ?? "null"}.{column.SourceColumn ?? "null"}).",
                    string.Equals(column.SourceTable, table, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(column.SourceColumn, sourceColumn, StringComparison.OrdinalIgnoreCase));

            void RequireNoSource(string label, ColumnInfo column) =>
                Require(fixture, "result column sources",
                    $"{label}: \"{column.Name}\" reports no source (got {column.SourceSchema ?? "null"}.{column.SourceTable ?? "null"}.{column.SourceColumn ?? "null"}).",
                    column.SourceSchema is null && column.SourceTable is null && column.SourceColumn is null);

            var plain = await ColumnsAsync("plain columns", $"SELECT {id}, {name}, {amount} FROM {fixture.QualifiedTableName};");
            RequireSource("plain columns", plain[0], fixture.TableName, "id");
            RequireSource("plain columns", plain[1], fixture.TableName, "name");
            RequireSource("plain columns", plain[2], fixture.TableName, "amount");

            // A schema-qualified table reports its schema, so same-named tables in two schemas stay apart.
            // SQL Server reports the table as the query names it, so an unqualified table has no schema there.
            if (provider.Capabilities.SupportsSchemas)
            {
                var schema = provider.ProviderName == "SQL Server" ? "dbo" : "public";
                var qualified = await ColumnsAsync("schema-qualified",
                    $"SELECT {id} FROM {fixture.Quote(schema)}.{fixture.QualifiedTableName};");
                RequireSource("schema-qualified", qualified[0], fixture.TableName, "id");
                Require(fixture, "result column sources",
                    $"schema-qualified: \"id\" reports schema {schema} (got {qualified[0].SourceSchema ?? "null"}).",
                    string.Equals(qualified[0].SourceSchema, schema, StringComparison.OrdinalIgnoreCase));
            }

            // Resolving sources must not add columns: SQL Server's browse mode appends hidden key columns.
            var keyless = await provider.ExecuteQueryAsync(fixture.ConnectionString, $"SELECT {name} FROM {fixture.QualifiedTableName} WHERE {id} = 1;");
            Require(fixture, "result column sources",
                $"a result without its key column gains no hidden columns (got {keyless.ColumnInfos.Count} column(s), row width {(keyless.ObjectRows.Count == 1 ? keyless.ObjectRows[0].Length : -1)}; {keyless.Error?.Summary}).",
                !keyless.HasError && keyless.ColumnInfos.Count == 1 && keyless.ObjectRows.Count == 1 && keyless.ObjectRows[0].Length == 1);
            RequireSource("keyless", keyless.ColumnInfos[0], fixture.TableName, "name");

            var star = await ColumnsAsync("SELECT *", $"SELECT * FROM {fixture.QualifiedTableName};");
            Require(fixture, "result column sources", $"SELECT *: three columns (got {star.Count}).", star.Count == 3);
            foreach (var column in star)
                RequireSource("SELECT *", column, fixture.TableName, column.Name);

            // A column alias renames the result column but keeps its source; a table alias resolves to the real table.
            var aliased = await ColumnsAsync("aliases",
                $"SELECT t.{name} AS {fixture.Quote("label")}, t.{id} FROM {fixture.QualifiedTableName} t;");
            Require(fixture, "result column sources", $"aliases: the result column takes its alias (got {aliased[0].Name}).",
                aliased[0].Name == "label");
            RequireSource("aliases", aliased[0], fixture.TableName, "name");
            RequireSource("aliases", aliased[1], fixture.TableName, "id");

            var expressions = await ColumnsAsync("expressions",
                $"SELECT {amount} + 1 AS {fixture.Quote("bumped")}, 'k' AS {fixture.Quote("literal")}, COUNT(*) AS {fixture.Quote("total")} FROM {fixture.QualifiedTableName} GROUP BY {amount};");
            foreach (var column in expressions)
                RequireNoSource("expressions", column);

            try
            {
                await provider.ExecuteNonQueryAsync(fixture.ConnectionString,
                    $"CREATE TABLE {otherTable} ({id} INTEGER PRIMARY KEY, {tag} VARCHAR(50) NULL);");
                await provider.ExecuteNonQueryAsync(fixture.ConnectionString, $"INSERT INTO {otherTable} ({id}, {tag}) VALUES (1, 'first');");

                // Each joined column reports its own table, so a join can be told apart from a single-table result.
                var joined = await ColumnsAsync("joins",
                    $"SELECT a.{id}, a.{name}, b.{id}, b.{tag} FROM {fixture.QualifiedTableName} a JOIN {otherTable} b ON b.{id} = a.{id};");
                RequireSource("joins", joined[0], fixture.TableName, "id");
                RequireSource("joins", joined[1], fixture.TableName, "name");
                RequireSource("joins", joined[2], otherTableName, "id");
                RequireSource("joins", joined[3], otherTableName, "tag");

                // Hidden key columns must not change what DISTINCT collapses.
                await provider.ExecuteNonQueryAsync(fixture.ConnectionString, $"INSERT INTO {otherTable} ({id}, {tag}) VALUES (2, 'first');");
                var distinct = await provider.ExecuteQueryAsync(fixture.ConnectionString, $"SELECT DISTINCT {tag} FROM {otherTable};");
                Require(fixture, "result column sources",
                    $"DISTINCT over a duplicated value returns one row and one column (got {distinct.ObjectRows.Count} row(s), {distinct.ColumnInfos.Count} column(s); {distinct.Error?.Summary}).",
                    !distinct.HasError && distinct.ObjectRows.Count == 1 && distinct.ColumnInfos.Count == 1);
            }
            finally
            {
                await provider.ExecuteNonQueryAsync(fixture.ConnectionString, $"DROP TABLE IF EXISTS {otherTable};");
            }
        });

        await CheckAsync(fixture, "editable query results", "QueryResultEditorViewModel commit (RAIN-P2-16)", async () =>
        {
            // A single-table result with its key commits edits through the change tracker on the tab's
            // session; a join or a keyless result stays read-only with a reason.
            if (!provider.Capabilities.ReportsResultColumnSources)
                return;

            var connection = new ConnectionInfo { Name = "conformance", Provider = provider.ProviderName, ConnectionString = fixture.ConnectionString };
            var databaseSession = new DatabaseSession(connection, provider);
            await using var session = await provider.OpenSessionAsync(connection, CancellationToken.None);
            var tableName = $"rain_conformance_edit_{Guid.NewGuid():N}";
            var table = fixture.Quote(tableName);
            var id = fixture.Quote("id");
            var name = fixture.Quote("name");
            var amount = fixture.Quote("amount");

            async Task<(QueryResultEditorViewModel Editor, QueryResult Result, IReadOnlyList<QueryResultDisplayRow> Rows)> LoadAsync(string sql)
            {
                var result = await session.ExecuteQueryAsync(sql);
                Require(fixture, "editable query results", $"the query runs ({result.Error?.Summary}).", !result.HasError);
                var rows = QueryResultFormattingService.BuildDisplayRows(result);
                var editor = new QueryResultEditorViewModel(() => Task.FromResult(session));
                await editor.LoadAsync(result, rows, databaseSession);
                return (editor, result, rows);
            }

            async Task<string?> NameOfAsync(int key)
            {
                var result = await session.ExecuteQueryAsync($"SELECT {name} FROM {table} WHERE {id} = {key};");
                return result.ObjectRows.Count == 1 ? result.ObjectRows[0][0]?.ToString() : null;
            }

            async Task EditAndCommitAsync(string label, string sql, int key, string newName)
            {
                var (editor, result, rows) = await LoadAsync(sql);
                Require(fixture, "editable query results", $"{label}: the result is editable (reason: {editor.ReadOnlyReason ?? "none"}).", editor.IsEditable);
                var idOrdinal = result.IndexOfColumn("id");
                var nameOrdinal = result.IndexOfColumn("name");
                var row = editor.RowFor(rows.Single(r => Convert.ToInt32(r.RawValues[idOrdinal]) == key).DisplayValues)!;
                Require(fixture, "editable query results", $"{label}: the edit parses.", editor.SetCellText(row, nameOrdinal, newName) is null);

                CommitPreviewRequest? preview = null;
                editor.CommitPreviewRequested += request => preview = request;
                editor.RequestCommitCommand.Execute(null);
                Require(fixture, "editable query results", $"{label}: the preview shows an UPDATE of the table (got {preview?.SqlPreview ?? "no preview"}).",
                    preview?.SqlPreview.Contains("UPDATE", StringComparison.Ordinal) == true && preview.SqlPreview.Contains(tableName, StringComparison.Ordinal));

                await editor.ExecuteCommitAsync();
                Require(fixture, "editable query results", $"{label}: the commit succeeds ({editor.StatusMessage}).", !editor.HasChanges);
                Require(fixture, "editable query results", $"{label}: the edit is persisted.", await NameOfAsync(key) == newName);
            }

            try
            {
                await provider.ExecuteNonQueryAsync(fixture.ConnectionString,
                    $"CREATE TABLE {table} ({id} INTEGER PRIMARY KEY, {name} VARCHAR(50) NULL, {amount} INTEGER NULL);");
                await provider.ExecuteNonQueryAsync(fixture.ConnectionString,
                    $"INSERT INTO {table} ({id}, {name}, {amount}) VALUES (1, 'Ada', 10);");
                await provider.ExecuteNonQueryAsync(fixture.ConnectionString,
                    $"INSERT INTO {table} ({id}, {name}, {amount}) VALUES (12, 'Grace', 20);");

                await EditAndCommitAsync("SELECT * with a filter", $"SELECT * FROM {table} WHERE {id} < 10;", 1, "Edited");

                if (provider.Capabilities.SupportsSchemas)
                {
                    var schema = provider.ProviderName == "SQL Server" ? "dbo" : "public";
                    await EditAndCommitAsync("schema-qualified", $"SELECT {id}, {name} FROM {fixture.Quote(schema)}.{table} WHERE {id} = 1;", 1, "Qualified");
                }

                var (joined, _, _) = await LoadAsync(
                    $"SELECT a.{id}, a.{name}, b.{amount} FROM {table} a JOIN {fixture.QualifiedTableName} b ON b.{id} = a.{id};");
                Require(fixture, "editable query results", $"a join is read-only with a reason (got {joined.ReadOnlyReason ?? "none"}).",
                    !joined.IsEditable && !string.IsNullOrEmpty(joined.ReadOnlyReason));

                var (keyless, _, _) = await LoadAsync($"SELECT {name}, {amount} FROM {table};");
                Require(fixture, "editable query results", $"a result without the key is read-only with a reason (got {keyless.ReadOnlyReason ?? "none"}).",
                    !keyless.IsEditable && keyless.ReadOnlyReason?.Contains("id", StringComparison.OrdinalIgnoreCase) == true);
            }
            finally
            {
                await provider.ExecuteNonQueryAsync(fixture.ConnectionString, $"DROP TABLE IF EXISTS {table};");
            }
        });

        await CheckAsync(fixture, "parameterized DML", "ExecuteTransactionAsync(SqlCommandSpec)", async () =>
        {
            var p0 = fixture.ParameterPlaceholder(0);
            var p1 = fixture.ParameterPlaceholder(1);
            var p2 = fixture.ParameterPlaceholder(2);
            var command = new SqlCommandSpec(
                $"INSERT INTO {fixture.QualifiedTableName} ({fixture.Quote("id")}, {fixture.Quote("name")}, {fixture.Quote("amount")}) VALUES ({p0}, {p1}, {p2});",
                fixture.ProviderName == "DuckDB"
                    ? new Dictionary<string, object?> { ["p0"] = 3, ["p1"] = "Linus", ["p2"] = 30 }
                    : new Dictionary<string, object?> { ["@p0"] = 3, ["@p1"] = "Linus", ["@p2"] = 30 });
            var transaction = await provider.ExecuteTransactionAsync(fixture.ConnectionString, [command]);
            Require(fixture, "parameterized DML", $"the insert transaction succeeded ({transaction.ErrorMessage ?? "no error details"}).", transaction.Succeeded);

            var result = await provider.ExecuteQueryAsync(
                fixture.ConnectionString,
                $"SELECT {fixture.Quote("name")} FROM {fixture.QualifiedTableName} WHERE {fixture.Quote("id")} = 3;");
            Require(fixture, "parameterized DML", "the bound value was persisted.", !result.HasError && result.ObjectRows.Count == 1);
        });

        await CheckAsync(fixture, "multiple results", "ExecuteQueryMultipleAsync", async () =>
        {
            var results = new List<QueryResult>();
            await foreach (var result in provider.ExecuteQueryMultipleAsync(
                               fixture.ConnectionString,
                               "SELECT 1 AS first; SELECT 2 AS second;"))
                results.Add(result);

            Require(fixture, "multiple results", $"expected 2 result(s) from a two-statement batch, received {results.Count}.", results.Count == 2);

            // A batch of independent statements is one thing; a single routine that itself produces
            // several result sets over one round trip is the case native NextResultAsync draining exists for.
            if (provider.ProviderName == "SQL Server")
            {
                await provider.ExecuteNonQueryAsync(
                    fixture.ConnectionString,
                    "CREATE OR ALTER PROCEDURE rain_conformance_multi AS BEGIN SELECT 1 AS a; SELECT 2 AS b; SELECT 3 AS c; END;");
                try
                {
                    var procedureResults = new List<QueryResult>();
                    await foreach (var result in provider.ExecuteQueryMultipleAsync(fixture.ConnectionString, "EXEC rain_conformance_multi;"))
                        procedureResults.Add(result);
                    Require(fixture, "multiple results",
                        $"a three-result-set procedure surfaces 3 results, received {procedureResults.Count}.",
                        procedureResults.Count == 3);
                }
                finally
                {
                    await provider.ExecuteNonQueryAsync(fixture.ConnectionString, "DROP PROCEDURE IF EXISTS rain_conformance_multi;");
                }
            }

            if (provider.ProviderName == "MySQL")
            {
                await provider.ExecuteNonQueryAsync(
                    fixture.ConnectionString,
                    "CREATE PROCEDURE rain_conformance_multi() BEGIN SELECT 1 AS a; SELECT 2 AS b; END;");
                try
                {
                    var procedureResults = new List<QueryResult>();
                    await foreach (var result in provider.ExecuteQueryMultipleAsync(fixture.ConnectionString, "CALL rain_conformance_multi();"))
                        procedureResults.Add(result);
                    Require(fixture, "multiple results",
                        $"a two-result-set procedure surfaces 2 results, received {procedureResults.Count}.",
                        procedureResults.Count == 2);
                }
                finally
                {
                    await provider.ExecuteNonQueryAsync(fixture.ConnectionString, "DROP PROCEDURE IF EXISTS rain_conformance_multi;");
                }
            }
        });

        await CheckAsync(fixture, "routine execution", "ExecuteRoutineAsync populates the columnar shape", async () =>
        {
            // ExecuteRoutineAsync (the Procedure Runner dialog's execution path) builds its own
            // QueryResult per result set independently of ExecuteQueryMultipleAsync/DbReaderResults,
            // so it needs its own coverage: ColumnInfos/ObjectRows must be populated here too, or the
            // Procedure Runner's result grid (which reads ColumnInfos since RAIN-208) renders no columns.
            if (provider.ProviderName == "SQL Server")
            {
                await provider.ExecuteNonQueryAsync(
                    fixture.ConnectionString,
                    "CREATE OR ALTER PROCEDURE rain_conformance_routine AS BEGIN SELECT 1 AS a; SELECT 2 AS b, 3 AS c; END;");
                try
                {
                    var routineResult = await provider.ExecuteRoutineAsync(fixture.ConnectionString, "dbo", "rain_conformance_routine", "dbo.rain_conformance_routine", SchemaNodeType.Procedure, []);
                    Require(fixture, "routine execution", $"expected 2 result sets, received {routineResult.ResultSets.Count}.", routineResult.ResultSets.Count == 2);
                    Require(fixture, "routine execution",
                        $"each result set populates ColumnInfos/ObjectRows (counts: {string.Join(",", routineResult.ResultSets.Select(r => r.ColumnInfos.Count))}).",
                        routineResult.ResultSets.All(r => r.ColumnInfos.Count > 0 && r.ObjectRows.Count > 0));
                }
                finally
                {
                    await provider.ExecuteNonQueryAsync(fixture.ConnectionString, "DROP PROCEDURE IF EXISTS rain_conformance_routine;");
                }
            }

            if (provider.ProviderName == "MySQL")
            {
                await provider.ExecuteNonQueryAsync(
                    fixture.ConnectionString,
                    "CREATE PROCEDURE rain_conformance_routine() BEGIN SELECT 1 AS a; SELECT 2 AS b, 3 AS c; END;");
                try
                {
                    var routineResult = await provider.ExecuteRoutineAsync(fixture.ConnectionString, "", "rain_conformance_routine", "rain_conformance_routine", SchemaNodeType.Procedure, []);
                    Require(fixture, "routine execution", $"expected 2 result sets, received {routineResult.ResultSets.Count}.", routineResult.ResultSets.Count == 2);
                    Require(fixture, "routine execution",
                        $"each result set populates ColumnInfos/ObjectRows (counts: {string.Join(",", routineResult.ResultSets.Select(r => r.ColumnInfos.Count))}).",
                        routineResult.ResultSets.All(r => r.ColumnInfos.Count > 0 && r.ObjectRows.Count > 0));
                }
                finally
                {
                    await provider.ExecuteNonQueryAsync(fixture.ConnectionString, "DROP PROCEDURE IF EXISTS rain_conformance_routine;");
                }
            }

            // PostgreSQL's ExecuteRoutineAsync got the same ColumnInfos/ObjectRows fix as SQL Server
            // and MySQL above, but a plain CALL to a PostgreSQL procedure does not hand the caller a
            // SELECT-shaped result set the way EXEC/CALL do on those two providers (it needs an
            // explicit refcursor OUT parameter), so there is no equally simple procedure shape here
            // to assert ColumnInfos on without exercising unrelated refcursor-handling behavior.
        });

        await CheckAsync(fixture, "cancellation", "ExecuteQueryAsync(CancellationToken)", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var result = await provider.ExecuteQueryAsync(
                fixture.ConnectionString,
                "SELECT 1;",
                new QueryExecutionOptions { CancellationToken = cancellation.Token });
            Require(fixture, "cancellation", "a cancelled query reports IsCanceled.", result.Error?.IsCanceled == true);
        });

        await CheckAsync(fixture, "paging", "LoadTablePageAsync", async () =>
        {
            var page = await provider.LoadTablePageAsync(
                fixture.ConnectionString,
                fixture.TableName,
                ["id"],
                pageSize: 2,
                pageIndex: 0);
            Require(fixture, "paging", "the first page has two rows.", !page.Result.HasError && page.Result.ObjectRows.Count == 2);
            Require(fixture, "paging", "the page reports a following row.", page.HasNextPage);
        });

        if (provider.Capabilities.SupportsTableQuerySpec)
        {
            await CheckAsync(fixture, "table query spec", "LoadTablePageAsync(TableQuerySpec)", async () =>
            {
                async Task<List<string?>> NamesOnPageAsync(TableQuerySpec spec)
                {
                    var page = await provider.LoadTablePageAsync(fixture.ConnectionString, fixture.TableName, spec);
                    Require(fixture, "table query spec", "the page loads without error.", !page.Result.HasError);
                    return page.Result.ObjectRows.Select(r => r[page.Result.IndexOfColumn("name")]?.ToString()).ToList();
                }

                var descending = new[] { new TableSortKey("amount", Descending: true) };
                var first = await NamesOnPageAsync(new TableQuerySpec(descending, PageIndex: 0, PageSize: 1));
                var second = await NamesOnPageAsync(new TableQuerySpec(descending, PageIndex: 1, PageSize: 1));
                Require(fixture, "table query spec", "a descending sort orders the whole table across pages.",
                    first.SequenceEqual(["Linus"]) && second.SequenceEqual(["Grace"]));

                var multi = await NamesOnPageAsync(new TableQuerySpec(
                    [new TableSortKey("amount", Descending: true), new TableSortKey("id")], 0, 10));
                Require(fixture, "table query spec", "a multi-column sort returns every row in order.",
                    multi.SequenceEqual(["Linus", "Grace", "Ada"]));
            });

            await CheckAsync(fixture, "table query spec", "LoadTablePageAsync(TableQuerySpec filters)", async () =>
            {
                async Task<List<string?>> FilteredAsync(params TableFilter[] filters)
                {
                    var spec = new TableQuerySpec([new TableSortKey("id")], 0, 10) { Filters = filters };
                    var page = await provider.LoadTablePageAsync(fixture.ConnectionString, fixture.TableName, spec);
                    Require(fixture, "table query spec", $"the filtered page loads without error ({page.Result.ErrorMessage}).", !page.Result.HasError);
                    return page.Result.ObjectRows.Select(r => r[page.Result.IndexOfColumn("name")]?.ToString()).ToList();
                }

                Require(fixture, "table query spec", "a numeric comparison against a text value filters by number.",
                    (await FilteredAsync(new TableFilter("amount", TableFilterOperator.GreaterThan, "15")))
                        .SequenceEqual(["Grace", "Linus"]));
                Require(fixture, "table query spec", "contains ignores case.",
                    (await FilteredAsync(new TableFilter("name", TableFilterOperator.Contains, "RA")))
                        .SequenceEqual(["Grace"]));
                Require(fixture, "table query spec", "filters combine with AND.",
                    (await FilteredAsync(
                        new TableFilter("amount", TableFilterOperator.GreaterOrEqual, "20"),
                        new TableFilter("name", TableFilterOperator.StartsWith, "L")))
                        .SequenceEqual(["Linus"]));
                Require(fixture, "table query spec", "a value with a quote and SQL text is matched literally.",
                    (await FilteredAsync(new TableFilter("name", TableFilterOperator.Equals, "O'Brien'; DROP TABLE x; --"))).Count == 0);
                Require(fixture, "table query spec", "a LIKE wildcard in the value is matched literally.",
                    (await FilteredAsync(new TableFilter("name", TableFilterOperator.Contains, "%"))).Count == 0);
                Require(fixture, "table query spec", "IS NOT NULL keeps every row.",
                    (await FilteredAsync(new TableFilter("name", TableFilterOperator.IsNotNull))).Count == 3);

                var rawSpec = new TableQuerySpec([new TableSortKey("id")], 0, 10)
                {
                    RawWhere = $"{fixture.Quote("name")} = 'Ada' OR {fixture.Quote("name")} = 'Grace'",
                    Filters = [new TableFilter("amount", TableFilterOperator.GreaterThan, "15")]
                };
                var rawPage = await provider.LoadTablePageAsync(fixture.ConnectionString, fixture.TableName, rawSpec);
                Require(fixture, "table query spec", $"a raw WHERE ANDs with column filters as one group ({rawPage.Result.ErrorMessage}).",
                    !rawPage.Result.HasError
                    && rawPage.Result.ObjectRows.Select(r => r[rawPage.Result.IndexOfColumn("name")]?.ToString()).SequenceEqual(["Grace"]));
            });

            await CheckAsync(fixture, "table query spec", "CountTableRowsAsync (ticket 05)", async () =>
            {
                var unfiltered = await provider.CountTableRowsAsync(
                    fixture.ConnectionString, fixture.TableName, new TableQuerySpec([], 0, 10));
                Require(fixture, "table query spec", $"the unfiltered exact total counts every seeded row (got {unfiltered}).",
                    unfiltered == 3);

                var filtered = await provider.CountTableRowsAsync(
                    fixture.ConnectionString,
                    fixture.TableName,
                    new TableQuerySpec([], 0, 10) { Filters = [new TableFilter("amount", TableFilterOperator.GreaterThan, "15")] });
                Require(fixture, "table query spec", $"the exact total honors active filters (got {filtered}).", filtered == 2);

                // Sort and page index/size have no bearing on a row count.
                var sortedAndPaged = await provider.CountTableRowsAsync(
                    fixture.ConnectionString,
                    fixture.TableName,
                    new TableQuerySpec([new TableSortKey("amount", Descending: true)], PageIndex: 5, PageSize: 1));
                Require(fixture, "table query spec", $"sort and paging do not change the total (got {sortedAndPaged}).", sortedAndPaged == 3);
            });
        }

        await CheckAsync(fixture, "transaction", "ExecuteTransactionAsync rollback", async () =>
        {
            var update = $"UPDATE {fixture.QualifiedTableName} SET {fixture.Quote("amount")} = 999 WHERE {fixture.Quote("id")} = 1;";
            var invalid = $"INSERT INTO {fixture.Quote(fixture.TableName + "_missing")} ({fixture.Quote("id")}) VALUES (9);";
            var transaction = await provider.ExecuteTransactionAsync(fixture.ConnectionString, [update, invalid]);
            Require(fixture, "transaction", "the invalid batch fails.", !transaction.Succeeded);

            var result = await provider.ExecuteQueryAsync(
                fixture.ConnectionString,
                $"SELECT {fixture.Quote("amount")} FROM {fixture.QualifiedTableName} WHERE {fixture.Quote("id")} = 1;");
            var amount = result.ObjectRows.Count == 1 ? result.ObjectRows[0][result.IndexOfColumn("amount")]?.ToString() : null;
            Require(fixture, "transaction", "the failed batch leaves no partial update.", amount == "10");
        });

        await CheckAsync(fixture, "affected-row safety", "exact row-count conflict rollback", async () =>
        {
            var firstPlaceholder = fixture.ParameterPlaceholder(0);
            var stalePlaceholder = fixture.ParameterPlaceholder(1);
            var commands = new[]
            {
                new SqlCommandSpec(
                    $"UPDATE {fixture.QualifiedTableName} SET {fixture.Quote("amount")} = 111 WHERE {fixture.Quote("id")} = {firstPlaceholder};",
                    fixture.ProviderName == "DuckDB"
                        ? new Dictionary<string, object?> { ["p0"] = 1 }
                        : new Dictionary<string, object?> { ["@p0"] = 1 },
                    expectedRecordsAffected: 1),
                new SqlCommandSpec(
                    $"UPDATE {fixture.QualifiedTableName} SET {fixture.Quote("amount")} = 222 WHERE {fixture.Quote("id")} = {stalePlaceholder};",
                    fixture.ProviderName == "DuckDB"
                        ? new Dictionary<string, object?> { ["p0"] = 999 }
                        : new Dictionary<string, object?> { ["@p1"] = 999 },
                    expectedRecordsAffected: 1)
            };
            var transaction = await provider.ExecuteTransactionAsync(fixture.ConnectionString, commands);
            Require(fixture, "affected-row safety", "a zero-row command is a typed conflict.",
                !transaction.Succeeded && transaction.IsConcurrencyConflict &&
                transaction.ExpectedRecordsAffected == 1 && transaction.ActualRecordsAffected == 0);

            var result = await provider.ExecuteQueryAsync(
                fixture.ConnectionString,
                $"SELECT {fixture.Quote("amount")} FROM {fixture.QualifiedTableName} WHERE {fixture.Quote("id")} = 1;");
            var amount = result.ObjectRows.Count == 1 ? result.ObjectRows[0][result.IndexOfColumn("amount")]?.ToString() : null;
            Require(fixture, "affected-row safety", "the conflict rolled back earlier commands.", amount == "10");
        });

        await CheckAsync(fixture, "concurrency conflicts", "DataChangeTracker edited-cell checks (RAIN-P2-12)", async () =>
        {
            // Each provider's awkward-to-compare types (json/ntext/FLOAT, and a blank-padded CHAR)
            // sit beside plain columns, so the check must neither error nor falsely conflict on them.
            var (payload, ratio) = provider.ProviderName switch
            {
                "PostgreSQL" => ("JSON", "REAL"),
                "SQL Server" => ("NTEXT", "REAL"),
                "MySQL" => ("JSON", "FLOAT"),
                "DuckDB" => ("VARCHAR", "FLOAT"),
                _ => ("TEXT", "REAL")
            };
            var table = $"rain_conformance_conflict_{Guid.NewGuid():N}";
            var quotedTable = fixture.Quote(table);
            DataChangeTracker tracker = null!;

            async Task<EditableRow> LoadRowAsync()
            {
                var result = await provider.ExecuteQueryAsync(fixture.ConnectionString, $"SELECT * FROM {quotedTable};");
                Require(fixture, "concurrency conflicts", $"the row loads ({result.ErrorMessage}).", !result.HasError && result.ObjectRows.Count == 1);
                var columns = result.ColumnInfos.Select(c => c.Name).ToList();
                tracker = new DataChangeTracker(table, columns, ["id"], provider.Capabilities,
                    provider.Capabilities.NonComparableColumns(result.ColumnInfos));
                return new EditableRow(result.ToRowDictionary(result.ObjectRows[0]));
            }

            void Edit(EditableRow row, string column, object? value)
            {
                row.Values[column] = value;
                row.State = EditState.Modified;
            }

            Task<TransactionResult> CommitAsync(EditableRow row, out SqlCommandSpec command)
            {
                command = tracker.BuildCommand(row)!;
                return provider.ExecuteTransactionAsync(fixture.ConnectionString, [command]);
            }

            async Task<string?> ReadNameAsync()
            {
                var result = await provider.ExecuteQueryAsync(fixture.ConnectionString, $"SELECT {fixture.Quote("name")} FROM {quotedTable};");
                return result.ObjectRows.Count == 1 ? result.ObjectRows[0][0]?.ToString() : null;
            }

            try
            {
                await provider.ExecuteNonQueryAsync(fixture.ConnectionString, $"""
                    CREATE TABLE {quotedTable} (
                        {fixture.Quote("id")} INT PRIMARY KEY,
                        {fixture.Quote("name")} VARCHAR(50) NULL,
                        {fixture.Quote("payload")} {payload} NULL,
                        {fixture.Quote("ratio")} {ratio} NULL,
                        {fixture.Quote("code")} CHAR(5) NULL
                    );
                    """);
                await provider.ExecuteNonQueryAsync(fixture.ConnectionString,
                    $"INSERT INTO {quotedTable} VALUES (1, 'Ada', '{{\"a\":1}}', 1.1, 'ab');");

                var row = await LoadRowAsync();
                Edit(row, "name", "Grace");
                Edit(row, "payload", "{\"a\":2}");
                Edit(row, "ratio", 2.25);
                Edit(row, "code", "cd");
                var unchallenged = await CommitAsync(row, out _);
                Require(fixture, "concurrency conflicts", $"an update nobody else touched commits ({unchallenged.ErrorMessage}; SQL: {tracker.BuildStatement(row)}).",
                    unchallenged.Succeeded);

                row = await LoadRowAsync();
                Edit(row, "name", "mine");
                await provider.ExecuteNonQueryAsync(fixture.ConnectionString,
                    $"UPDATE {quotedTable} SET {fixture.Quote("name")} = 'theirs';");
                var conflicted = await CommitAsync(row, out var conflictedCommand);
                Require(fixture, "concurrency conflicts", "an edit to a cell changed underneath is a typed conflict naming its command.",
                    !conflicted.Succeeded && conflicted.IsConcurrencyConflict && ReferenceEquals(conflicted.FailedCommand, conflictedCommand));
                Require(fixture, "concurrency conflicts", "the conflict leaves the other user's value.", await ReadNameAsync() == "theirs");

                var rowQuery = tracker.BuildRowQuery(row);
                var current = await provider.ExecuteQueryAsync(fixture.ConnectionString, rowQuery.Sql, new QueryExecutionOptions { Parameters = rowQuery.Parameters });
                Require(fixture, "concurrency conflicts", $"the conflicted row reloads by its key ({current.ErrorMessage}).",
                    !current.HasError && current.ObjectRows.Count == 1 && current.ObjectRows[0][current.IndexOfColumn("name")]?.ToString() == "theirs");

                row.OverwritesConcurrentChanges = true;
                var overwritten = await CommitAsync(row, out _);
                Require(fixture, "concurrency conflicts", $"overwrite commits the user's edit ({overwritten.ErrorMessage}).",
                    overwritten.Succeeded && await ReadNameAsync() == "mine");
            }
            finally
            {
                await provider.ExecuteNonQueryAsync(fixture.ConnectionString, $"DROP TABLE IF EXISTS {quotedTable};");
            }
        });

        await CheckAsync(fixture, "inserts", "DataChangeTracker commands and preview literals (RAIN-P2-10)", async () =>
        {
            var sequence = $"rain_conformance_insert_seq_{Guid.NewGuid():N}";
            var (identity, boolean, timestamp) = provider.ProviderName switch
            {
                "SQLite" => ("INTEGER PRIMARY KEY", "BOOLEAN", "TIMESTAMP"),
                "PostgreSQL" => ("INT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY", "BOOLEAN", "TIMESTAMP"),
                "SQL Server" => ("INT IDENTITY(1,1) PRIMARY KEY", "BIT", "DATETIME2"),
                "MySQL" => ("INT AUTO_INCREMENT PRIMARY KEY", "BOOLEAN", "DATETIME(3)"),
                "DuckDB" => ($"INTEGER PRIMARY KEY DEFAULT nextval('{sequence}')", "BOOLEAN", "TIMESTAMP"),
                _ => throw new NotSupportedException($"No identity column DDL is defined for {provider.ProviderName}.")
            };
            var table = $"rain_conformance_insert_{Guid.NewGuid():N}";
            var quotedTable = fixture.Quote(table);
            string[] columns = ["id", "name", "status", "active", "amount", "created"];
            var tracker = new DataChangeTracker(table, columns, ["id"], provider.Capabilities);

            EditableRow NewRow(params (string Column, object? Value)[] edits)
            {
                var values = columns.ToDictionary(c => c, _ => (object?)null);
                foreach (var (column, value) in edits)
                    values[column] = value;
                return new EditableRow(values, editedColumns: edits.Select(e => e.Column)) { State = EditState.Inserted };
            }

            async Task CommitAsync(string label, EditableRow row)
            {
                var transaction = await provider.ExecuteTransactionAsync(fixture.ConnectionString, [tracker.BuildCommand(row)!]);
                Require(fixture, "inserts", $"{label} commits ({transaction.ErrorMessage}; SQL: {tracker.BuildStatement(row)}).", transaction.Succeeded);
            }

            async Task<object?[]?> ReadRowAsync(string where)
            {
                var result = await provider.ExecuteQueryAsync(fixture.ConnectionString,
                    $"SELECT {string.Join(", ", columns.Select(fixture.Quote))} FROM {quotedTable} WHERE {where};");
                Require(fixture, "inserts", $"the inserted row reads back ({result.ErrorMessage}).", !result.HasError);
                return result.ObjectRows.Count == 1 ? result.ObjectRows[0] : null;
            }

            try
            {
                if (provider.ProviderName == "DuckDB")
                    await provider.ExecuteNonQueryAsync(fixture.ConnectionString, $"CREATE SEQUENCE {sequence};");
                await provider.ExecuteNonQueryAsync(fixture.ConnectionString, $"""
                    CREATE TABLE {quotedTable} (
                        {fixture.Quote("id")} {identity},
                        {fixture.Quote("name")} VARCHAR(50) NULL,
                        {fixture.Quote("status")} VARCHAR(20) NOT NULL DEFAULT 'new',
                        {fixture.Quote("active")} {boolean} NULL,
                        {fixture.Quote("amount")} DECIMAL(18,4) NULL,
                        {fixture.Quote("created")} {timestamp} NULL
                    );
                    """);

                await CommitAsync("an insert leaving identity and default columns untouched", NewRow(("name", "untouched")));
                var untouched = await ReadRowAsync($"{fixture.Quote("name")} = 'untouched'");
                Require(fixture, "inserts", "the identity column is generated server-side.", untouched?[0] is not null);
                Require(fixture, "inserts", "the default column applies server-side.", untouched?[2]?.ToString() == "new");

                await CommitAsync("an insert explicitly setting a default column", NewRow(("name", "explicit default"), ("status", "vip")));
                var explicitDefault = await ReadRowAsync($"{fixture.Quote("name")} = 'explicit default'");
                Require(fixture, "inserts", "an explicitly set default column keeps the user's value.", explicitDefault?[2]?.ToString() == "vip");

                // SQL Server only accepts an explicit IDENTITY value under SET IDENTITY_INSERT, which the grid does not issue.
                if (provider.ProviderName != "SQL Server")
                {
                    await CommitAsync("an insert explicitly setting the identity column", NewRow(("id", 100), ("name", "explicit id")));
                    var explicitId = await ReadRowAsync($"{fixture.Quote("name")} = 'explicit id'");
                    Require(fixture, "inserts", "an explicitly set identity column keeps the user's value.",
                        explicitId is not null && Convert.ToInt64(explicitId[0]) == 100);
                }

                await CommitAsync("an insert with no edited column", NewRow());
                var allDefaults = await ReadRowAsync($"{fixture.Quote("name")} IS NULL");
                Require(fixture, "inserts", "an all-defaults insert creates a row with its defaults.", allDefaults?[2]?.ToString() == "new");

                var previewed = NewRow(("name", "preview"), ("active", true), ("amount", 1.5m), ("created", new DateTime(2026, 1, 2, 3, 4, 5, 125)));
                var preview = tracker.BuildStatement(previewed);
                try
                {
                    await provider.ExecuteNonQueryAsync(fixture.ConnectionString, preview);
                }
                catch (Exception ex)
                {
                    Require(fixture, "inserts", $"the commit preview's INSERT runs as-is ({ex.Message}; SQL: {preview}).", false);
                }
                var readBack = await ReadRowAsync($"{fixture.Quote("name")} = 'preview'");
                Require(fixture, "inserts", "the preview's boolean literal round-trips.", readBack is not null && Convert.ToBoolean(readBack[3]));
                Require(fixture, "inserts", "the preview's decimal literal round-trips.", readBack is not null && Convert.ToDecimal(readBack[4]) == 1.5m);
            }
            finally
            {
                await provider.ExecuteNonQueryAsync(fixture.ConnectionString, $"DROP TABLE IF EXISTS {quotedTable};");
                if (provider.ProviderName == "DuckDB")
                    await provider.ExecuteNonQueryAsync(fixture.ConnectionString, $"DROP SEQUENCE IF EXISTS {sequence};");
            }
        });

        await CheckAsync(fixture, "enum/json columns", "ENUM and JSON/JSONB round-trip (RAIN-P2-09)", async () =>
        {
            if (provider.ProviderName == "MySQL")
            {
                var table = $"rain_conformance_enumjson_{Guid.NewGuid():N}";
                var quotedTable = fixture.Quote(table);
                try
                {
                    await provider.ExecuteNonQueryAsync(fixture.ConnectionString, $"""
                        CREATE TABLE {quotedTable} (
                            {fixture.Quote("id")} INT PRIMARY KEY,
                            {fixture.Quote("status")} ENUM('active','retired') NOT NULL,
                            {fixture.Quote("flags")} SET('read','write') NOT NULL,
                            {fixture.Quote("payload")} JSON NOT NULL
                        );
                        """);
                    await provider.ExecuteNonQueryAsync(fixture.ConnectionString,
                        $"INSERT INTO {quotedTable} ({fixture.Quote("id")}, {fixture.Quote("status")}, {fixture.Quote("flags")}, {fixture.Quote("payload")}) " +
                        "VALUES (1, 'active', 'read,write', '{\"a\":1}');");

                    var enumColumns = await provider.GetEnumColumnsAsync(fixture.ConnectionString, table);
                    Require(fixture, "enum/json columns", "ENUM values are reported from provider metadata.",
                        enumColumns.TryGetValue("status", out var status) &&
                        status.Values.SequenceEqual(["active", "retired"]) && !status.AllowsMultipleValues);
                    Require(fixture, "enum/json columns", "SET values are reported from provider metadata.",
                        enumColumns.TryGetValue("flags", out var flags) &&
                        flags.Values.SequenceEqual(["read", "write"]) && flags.AllowsMultipleValues);

                    var result = await provider.ExecuteQueryAsync(fixture.ConnectionString,
                        $"SELECT {fixture.Quote("status")}, {fixture.Quote("flags")}, {fixture.Quote("payload")} FROM {quotedTable} WHERE {fixture.Quote("id")} = 1;");
                    Require(fixture, "enum/json columns", $"the row reads back ({result.ErrorMessage}).", !result.HasError && result.ObjectRows.Count == 1);
                    var row = result.ObjectRows[0];
                    Require(fixture, "enum/json columns", "ENUM round-trips exactly.", row[0]?.ToString() == "active");
                    Require(fixture, "enum/json columns", "SET round-trips exactly.", row[1]?.ToString() == "read,write");
                    using var payload = System.Text.Json.JsonDocument.Parse(
                        CellValueConverter.Parse(row[2]?.ToString(), ColumnDataType.Json)!.ToString()!);
                    Require(fixture, "enum/json columns", "JSON content round-trips (whitespace may be normalized).",
                        payload.RootElement.EnumerateObject().Count() == 1 && payload.RootElement.GetProperty("a").GetInt32() == 1);
                }
                finally
                {
                    await provider.ExecuteNonQueryAsync(fixture.ConnectionString, $"DROP TABLE IF EXISTS {quotedTable};");
                }
            }

            if (provider.ProviderName == "PostgreSQL")
            {
                var suffix = Guid.NewGuid().ToString("N");
                var enumType = $"rain_conformance_mood_{suffix}";
                var table = $"rain_conformance_enumjson_{suffix}";
                var quotedTable = fixture.Quote(table);
                var quotedType = fixture.Quote(enumType);
                try
                {
                    await provider.ExecuteNonQueryAsync(fixture.ConnectionString, $"CREATE TYPE {quotedType} AS ENUM ('happy','sad');");
                    await provider.ExecuteNonQueryAsync(fixture.ConnectionString, $"""
                        CREATE TABLE {quotedTable} (
                            {fixture.Quote("id")} INT PRIMARY KEY,
                            {fixture.Quote("mood")} {quotedType} NOT NULL,
                            {fixture.Quote("payload")} JSONB NOT NULL
                        );
                        """);
                    await provider.ExecuteNonQueryAsync(fixture.ConnectionString,
                        $"INSERT INTO {quotedTable} ({fixture.Quote("id")}, {fixture.Quote("mood")}, {fixture.Quote("payload")}) " +
                        "VALUES (1, 'happy', '{\"a\": 1}');");

                    var enumColumns = await provider.GetEnumColumnsAsync(fixture.ConnectionString, table);
                    Require(fixture, "enum/json columns", "native ENUM values are reported from provider metadata.",
                        enumColumns.TryGetValue("mood", out var mood) &&
                        mood.Values.SequenceEqual(["happy", "sad"]) && !mood.AllowsMultipleValues);

                    var result = await provider.ExecuteQueryAsync(fixture.ConnectionString,
                        $"SELECT {fixture.Quote("mood")}, {fixture.Quote("payload")} FROM {quotedTable} WHERE {fixture.Quote("id")} = 1;");
                    Require(fixture, "enum/json columns", $"the row reads back ({result.ErrorMessage}).", !result.HasError && result.ObjectRows.Count == 1);
                    var row = result.ObjectRows[0];
                    Require(fixture, "enum/json columns", "native ENUM round-trips exactly.", row[0]?.ToString() == "happy");
                    using var payload = System.Text.Json.JsonDocument.Parse(
                        CellValueConverter.Parse(row[1]?.ToString(), ColumnDataType.Json)!.ToString()!);
                    Require(fixture, "enum/json columns", "JSONB content round-trips (order/whitespace may be normalized).",
                        payload.RootElement.EnumerateObject().Count() == 1 && payload.RootElement.GetProperty("a").GetInt32() == 1);
                }
                finally
                {
                    await provider.ExecuteNonQueryAsync(fixture.ConnectionString, $"DROP TABLE IF EXISTS {quotedTable};");
                    await provider.ExecuteNonQueryAsync(fixture.ConnectionString, $"DROP TYPE IF EXISTS {quotedType};");
                }
            }
        });

        await CheckAsync(fixture, "insert only edited columns", "DataChangeTracker (RAIN-P2-10)", async () =>
        {
            // A self-contained table (not the shared fixture table) with an identity PK and a
            // server-side default, so untouched columns can be proven to apply server-side rather
            // than clashing with the shared table's plain INTEGER PRIMARY KEY.
            var table = $"rain_conformance_insert_{Guid.NewGuid():N}";
            var quotedTable = fixture.Quote(table);
            var sequence = $"{table}_seq";
            try
            {
                if (provider.ProviderName == "DuckDB")
                    await provider.ExecuteNonQueryAsync(fixture.ConnectionString, $"CREATE SEQUENCE {sequence};");
                await provider.ExecuteNonQueryAsync(fixture.ConnectionString, ProviderInsertDefaultsDdl(provider.ProviderName, table, sequence));

                var tracker = new DataChangeTracker(table, ["id", "name", "amount"], ["id"], provider.Capabilities);

                var untouchedRow = new EditableRow(
                    new Dictionary<string, object?> { ["id"] = null, ["name"] = "Untouched", ["amount"] = null },
                    editedColumns: ["name"])
                { State = EditState.Inserted };
                var untouchedSql = tracker.BuildStatement(untouchedRow);
                Require(fixture, "insert only edited columns", $"the INSERT omits untouched columns (got: {untouchedSql}).",
                    !untouchedSql.Contains("amount", StringComparison.OrdinalIgnoreCase) &&
                    !untouchedSql.Contains(fixture.Quote("id"), StringComparison.Ordinal));
                await provider.ExecuteNonQueryAsync(fixture.ConnectionString, untouchedSql);

                var untouchedRead = await provider.ExecuteQueryAsync(fixture.ConnectionString,
                    $"SELECT {fixture.Quote("id")}, {fixture.Quote("amount")} FROM {quotedTable} WHERE {fixture.Quote("name")} = 'Untouched';");
                Require(fixture, "insert only edited columns", $"the row reads back ({untouchedRead.ErrorMessage}).",
                    !untouchedRead.HasError && untouchedRead.ObjectRows.Count == 1);
                var untouchedRowValues = untouchedRead.ObjectRows[0];
                Require(fixture, "insert only edited columns", "an untouched identity column is assigned server-side.",
                    untouchedRowValues[untouchedRead.IndexOfColumn("id")] is not null);
                Require(fixture, "insert only edited columns", $"an untouched default column applies its default server-side (got {untouchedRowValues[untouchedRead.IndexOfColumn("amount")]}).",
                    Convert.ToInt32(untouchedRowValues[untouchedRead.IndexOfColumn("amount")]) == 42);

                var explicitRow = new EditableRow(
                    new Dictionary<string, object?> { ["id"] = null, ["name"] = null, ["amount"] = 7 },
                    editedColumns: ["amount"])
                { State = EditState.Inserted };
                var explicitSql = tracker.BuildStatement(explicitRow);
                await provider.ExecuteNonQueryAsync(fixture.ConnectionString, explicitSql);

                var explicitRead = await provider.ExecuteQueryAsync(fixture.ConnectionString,
                    $"SELECT {fixture.Quote("name")}, {fixture.Quote("amount")} FROM {quotedTable} WHERE {fixture.Quote("amount")} = 7;");
                Require(fixture, "insert only edited columns", $"the explicitly-set row reads back ({explicitRead.ErrorMessage}).",
                    !explicitRead.HasError && explicitRead.ObjectRows.Count == 1);
                var explicitRowValues = explicitRead.ObjectRows[0];
                Require(fixture, "insert only edited columns", $"an explicitly set default-column value overrides the default (got {explicitRowValues[explicitRead.IndexOfColumn("amount")]}).",
                    Convert.ToInt32(explicitRowValues[explicitRead.IndexOfColumn("amount")]) == 7);
                Require(fixture, "insert only edited columns", "an untouched nullable column with no default reads back as NULL.",
                    explicitRowValues[explicitRead.IndexOfColumn("name")] is null);

                // A row where the user edited nothing at all (e.g. AddRow followed by an immediate
                // commit) has no columns to name, so this must fall back to a bare DEFAULT VALUES
                // insert (or MySQL's "() VALUES ()") rather than an empty column/value list mismatch.
                var blankRow = new EditableRow(new Dictionary<string, object?> { ["id"] = null, ["name"] = null, ["amount"] = null })
                { State = EditState.Inserted };
                var blankSql = tracker.BuildStatement(blankRow);
                await provider.ExecuteNonQueryAsync(fixture.ConnectionString, blankSql);

                var blankRead = await provider.ExecuteQueryAsync(fixture.ConnectionString,
                    $"SELECT {fixture.Quote("amount")} FROM {quotedTable} WHERE {fixture.Quote("amount")} = 42 AND {fixture.Quote("name")} IS NULL;");
                Require(fixture, "insert only edited columns", $"a fully untouched insert ({blankSql}) succeeds and reads back ({blankRead.ErrorMessage}).",
                    !blankRead.HasError && blankRead.ObjectRows.Count == 1);
                Require(fixture, "insert only edited columns", $"its default column still applies its default (got {blankRead.ObjectRows[0][blankRead.IndexOfColumn("amount")]}).",
                    Convert.ToInt32(blankRead.ObjectRows[0][blankRead.IndexOfColumn("amount")]) == 42);
            }
            finally
            {
                await provider.ExecuteNonQueryAsync(fixture.ConnectionString, $"DROP TABLE IF EXISTS {quotedTable};");
                if (provider.ProviderName == "DuckDB")
                    await provider.ExecuteNonQueryAsync(fixture.ConnectionString, $"DROP SEQUENCE IF EXISTS {fixture.Quote(sequence)};");
            }
        });

        await CheckAsync(fixture, "DDL retrieval", "GetViewDefinitionAsync", async () =>
        {
            if (!provider.Capabilities.SupportsViews)
                return;

            var definition = await provider.GetViewDefinitionAsync(fixture.ConnectionString, fixture.ViewName);
            Require(fixture, "DDL retrieval", "the seeded view definition is available.",
                !string.IsNullOrWhiteSpace(definition) &&
                definition.Contains(fixture.ViewName, StringComparison.OrdinalIgnoreCase));
        });

        await CheckAsync(fixture, "explain", "ExplainPrefix/NormalizeExplainResult", async () =>
        {
            if (!provider.Capabilities.SupportsAnalyzeExplain)
                return;

            var result = await provider.ExecuteQueryAsync(
                fixture.ConnectionString,
                $"{provider.ExplainPrefix} SELECT {fixture.Quote("id")} FROM {fixture.QualifiedTableName};");
            Require(fixture, "explain", "the declared explain path executes.", !result.HasError && result.HasResultSet);

            var normalized = provider.NormalizeExplainResult(result);
            Require(fixture, "explain", "the normalized explain result has a result set.", !normalized.HasError && normalized.HasResultSet);
            Require(fixture, "explain",
                $"the normalized explain result populates the columnar shape (columns: {normalized.ColumnInfos.Count}, rows: {normalized.ObjectRows.Count}).",
                normalized.ColumnInfos.Count > 0 && normalized.ObjectRows.Count > 0);
        });

        await CheckAsync(fixture, "unsupported behavior", "optional defaults", async () =>
        {
            if (!provider.Capabilities.SupportsFunctions)
            {
                var definition = await provider.GetFunctionDefinitionAsync(fixture.ConnectionString, "main", "missing_function");
                Require(fixture, "unsupported behavior", "unsupported functions return no definition.", definition is null);
            }

            if (!provider.Capabilities.SupportsProcedures)
            {
                var definition = await provider.GetProcedureDefinitionAsync(fixture.ConnectionString, "main", "missing_procedure");
                Require(fixture, "unsupported behavior", "unsupported procedures return no definition.", definition is null);
                var parameters = await provider.GetProcedureParametersAsync(fixture.ConnectionString, "main", "missing_procedure");
                Require(fixture, "unsupported behavior", "unsupported procedures return no parameters.", parameters.Count == 0);
            }

            if (!provider.Capabilities.SupportsObjectComments)
            {
                var comment = await provider.GetTableCommentAsync(fixture.ConnectionString, fixture.TableName);
                var columnComments = await provider.GetColumnCommentsAsync(fixture.ConnectionString, fixture.TableName);
                Require(fixture, "unsupported behavior", "unsupported comments read as absent.", comment is null && columnComments.Count == 0);
                await provider.SetTableCommentAsync(fixture.ConnectionString, fixture.TableName, "not supported");
                await provider.SetColumnCommentAsync(fixture.ConnectionString, fixture.TableName, "name", "not supported");
            }

            if (!provider.Capabilities.SupportsUserWrite)
            {
                Require(fixture, "unsupported behavior", "unsupported user SQL is empty.",
                    provider.GenerateCreateUserSql("rain_user", "secret") == string.Empty &&
                    provider.GenerateDropUserSql("rain_user") == string.Empty);
                await Assert.ThrowsAsync<NotSupportedException>(() =>
                    provider.CreateUserAsync(fixture.ConnectionString, "rain_user", "secret"));
            }
        });
    }

    /// <summary>
    /// Session contract: a session is one connection that keeps its own
    /// session-scoped state and transaction, isolated from other sessions.
    /// </summary>
    public static async Task RunSessionAsync(ProviderConformanceFixture fixture)
    {
        var provider = fixture.Provider;
        var dialect = ProviderSessionDialect.For(provider.ProviderName);
        var connection = new ConnectionInfo
        {
            Name = "conformance",
            Provider = provider.ProviderName,
            ConnectionString = fixture.ConnectionString
        };
        var table = fixture.QualifiedTableName;
        var id = fixture.Quote("id");
        var name = fixture.Quote("name");
        var amount = fixture.Quote("amount");
        var tempTable = $"{dialect.TempTablePrefix}rain_temp_{Guid.NewGuid():N}";

        // Row 100 is the marker the transaction checks write, read and remove.
        var insertTxRow = $"INSERT INTO {table} ({id}, {name}, {amount}) VALUES (100, 'Tx', 1);";
        var selectTxRow = $"SELECT {id} FROM {table}{dialect.TableHint} WHERE {id} = 100;";
        var deleteTxRow = $"DELETE FROM {table} WHERE {id} = 100;";

        await using var first = await provider.OpenSessionAsync(connection, CancellationToken.None);
        await using var second = await provider.OpenSessionAsync(connection, CancellationToken.None);

        await CheckAsync(fixture, "session parameters", "text values bind to typed columns", async () =>
        {
            // The app binds filter values and JSON/enum cell edits as strings; a strictly typed
            // dialect must still resolve them against the column's type, as it would a literal.
            if (provider.Capabilities.SupportsTableQuerySpec)
            {
                var spec = new TableQuerySpec([new TableSortKey("id")], 0, 10)
                {
                    Filters = [new TableFilter("amount", TableFilterOperator.GreaterThan, "15")]
                };
                var page = await first.LoadTablePageAsync(fixture.TableName, spec);
                Require(fixture, "session parameters", $"a numeric filter with a text value loads ({page.Result.ErrorMessage}).",
                    !page.Result.HasError);
            }

            var jsonType = provider.ProviderName switch
            {
                "PostgreSQL" or "MySQL" => "JSON",
                "SQL Server" => "NVARCHAR(MAX)",
                "DuckDB" => "VARCHAR",
                _ => "TEXT"
            };
            var jsonTable = fixture.Quote($"rain_conformance_json_{Guid.NewGuid():N}");
            try
            {
                await provider.ExecuteNonQueryAsync(fixture.ConnectionString,
                    $"CREATE TABLE {jsonTable} ({id} INT PRIMARY KEY, {fixture.Quote("payload")} {jsonType} NULL);");
                await provider.ExecuteNonQueryAsync(fixture.ConnectionString, $"INSERT INTO {jsonTable} VALUES (1, NULL);");
                var update = await first.ExecuteTransactionAsync(
                [
                    new SqlCommandSpec(
                        $"UPDATE {jsonTable} SET {fixture.Quote("payload")} = {fixture.ParameterPlaceholder(0)} WHERE {id} = 1;",
                        new Dictionary<string, object?> { [provider.Capabilities.CreateParameterName(0)] = "{\"a\":1}" },
                        expectedRecordsAffected: 1)
                ]);
                Require(fixture, "session parameters", $"a text value binds to a JSON column ({update.ErrorMessage}).", update.Succeeded);
            }
            finally
            {
                await provider.ExecuteNonQueryAsync(fixture.ConnectionString, $"DROP TABLE IF EXISTS {jsonTable};");
            }
        });

        // Inserts row 100 in the open transaction and proves the owner sees it and the other session does not.
        async Task AssertUncommittedRowIsolatedAsync()
        {
            var insert = await first.ExecuteQueryAsync(insertTxRow);
            Require(fixture, "session transaction", $"the insert runs ({insert.Error?.Summary}).", !insert.HasError);

            var own = await first.ExecuteQueryAsync(selectTxRow);
            var foreign = await second.ExecuteQueryAsync(selectTxRow);
            Require(fixture, "session transaction", "the owning session sees its uncommitted row.", own.ObjectRows.Count == 1);
            Require(fixture, "session transaction",
                $"another session does not see the uncommitted row ({foreign.Error?.Summary}).",
                !foreign.HasError && foreign.ObjectRows.Count == 0);
        }

        try
        {
            await CheckAsync(fixture, "session state", "temp table persists across runs", async () =>
            {
                var create = await first.ExecuteQueryAsync(dialect.CreateTempTable(tempTable));
                Require(fixture, "session state", $"the temp table is created ({create.Error?.Summary}).", !create.HasError);
                var insert = await first.ExecuteQueryAsync($"INSERT INTO {tempTable} (val) VALUES (7);");
                Require(fixture, "session state", $"the temp row is inserted ({insert.Error?.Summary}).", !insert.HasError);

                var sameSession = await first.ExecuteQueryAsync($"SELECT val FROM {tempTable};");
                Require(fixture, "session state", "a later run in the same session sees the temp table.",
                    !sameSession.HasError && sameSession.ObjectRows.Count == 1);

                var otherSession = await second.ExecuteQueryAsync($"SELECT val FROM {tempTable};");
                Require(fixture, "session state", "another session does not see the temp table.", otherSession.HasError);
            });

            await CheckAsync(fixture, "result detection", "statements that return rows display them", async () =>
            {
                foreach (var form in dialect.ResultSetForms)
                {
                    if (form.Setup is { } setup)
                    {
                        var created = await first.ExecuteQueryAsync(setup);
                        Require(fixture, "result detection", $"{form.Label}: setup runs ({created.Error?.Summary}).", !created.HasError);
                    }

                    try
                    {
                        var result = await first.ExecuteQueryAsync(form.Sql.Replace("{temp}", tempTable).Replace("{table}", table));
                        Require(fixture, "result detection", $"{form.Label}: runs ({result.Error?.Summary}).", !result.HasError);
                        Require(fixture, "result detection", $"{form.Label}: reports a result set.", result.HasResultSet);
                        Require(fixture, "result detection",
                            $"{form.Label}: returns at least {form.MinRows} row(s), got {result.ObjectRows.Count}.",
                            result.ObjectRows.Count >= form.MinRows);
                    }
                    finally
                    {
                        if (form.Cleanup is { } cleanup)
                            await first.ExecuteQueryAsync(cleanup);
                    }
                }
            });

            await CheckAsync(fixture, "result detection", "statements without a result set report rows affected", async () =>
            {
                var insert = await first.ExecuteQueryAsync($"INSERT INTO {tempTable} (val) VALUES (8), (9);");
                Require(fixture, "result detection", $"the insert runs ({insert.Error?.Summary}).", !insert.HasError);
                Require(fixture, "result detection", "an insert reports no result set.", !insert.HasResultSet);
                var delete = await first.ExecuteQueryAsync($"DELETE FROM {tempTable} WHERE val IN (8, 9);");
                var none = await first.ExecuteQueryAsync($"DELETE FROM {tempTable} WHERE val = -1;");
                Require(fixture, "result detection", $"the deletes run ({delete.Error?.Summary}, {none.Error?.Summary}).", !delete.HasError && !none.HasError);

                if (!dialect.ReportsRowsAffected)
                {
                    Require(fixture, "result detection", "a driver without a count reports it as unknown, not as 0.",
                        insert.RecordsAffected < 0 && delete.RecordsAffected < 0);
                    return;
                }

                Require(fixture, "result detection", $"an insert of two rows reports 2 affected, got {insert.RecordsAffected}.", insert.RecordsAffected == 2);
                Require(fixture, "result detection", $"a delete of two rows reports 2 affected, got {delete.RecordsAffected}.", delete.RecordsAffected == 2);
                Require(fixture, "result detection", "a delete matching nothing reports 0 affected.", none.RecordsAffected == 0);
            });

            await CheckAsync(fixture, "session state", "SET options persist across runs", async () =>
            {
                if (dialect.SetProbe is not { } probe)
                    return;

                var apply = await first.ExecuteQueryAsync(probe.Set);
                Require(fixture, "session state", $"the SET runs ({apply.Error?.Summary}).", !apply.HasError);
                var own = await first.ExecuteQueryAsync(probe.Read);
                var other = await second.ExecuteQueryAsync(probe.Read);
                Require(fixture, "session state", "a later run in the same session sees the SET.",
                    own.ObjectRows.Count == 1 && own.ObjectRows[0][0]?.ToString() == probe.Expected);
                Require(fixture, "session state", "another session does not see the SET.",
                    other.ObjectRows.Count == 1 && other.ObjectRows[0][0]?.ToString() != probe.Expected);
            });

            await CheckAsync(fixture, "session transaction", "BEGIN and ROLLBACK typed as separate runs", async () =>
            {
                var begin = await first.ExecuteQueryAsync(dialect.BeginSql);
                Require(fixture, "session transaction", $"BEGIN runs ({begin.Error?.Summary}).", !begin.HasError);
                await AssertUncommittedRowIsolatedAsync();

                var rollback = await first.ExecuteQueryAsync("ROLLBACK;");
                Require(fixture, "session transaction", $"ROLLBACK runs ({rollback.Error?.Summary}).", !rollback.HasError);
                var afterRollback = await first.ExecuteQueryAsync(selectTxRow);
                Require(fixture, "session transaction", "the rolled back row is gone.", afterRollback.ObjectRows.Count == 0);
            });

            await CheckAsync(fixture, "session transaction", "session transaction API isolates and commits", async () =>
            {
                Require(fixture, "session transaction", "a new session is not in a transaction.", !first.IsInTransaction);

                await first.BeginTransactionAsync();
                Require(fixture, "session transaction", "BeginTransactionAsync opens a transaction.", first.IsInTransaction);
                await AssertUncommittedRowIsolatedAsync();

                await first.RollbackAsync();
                Require(fixture, "session transaction", "RollbackAsync closes the transaction.", !first.IsInTransaction);
                var afterRollback = await first.ExecuteQueryAsync(selectTxRow);
                Require(fixture, "session transaction", "a rolled back row is gone.", afterRollback.ObjectRows.Count == 0);

                await first.BeginTransactionAsync();
                await first.ExecuteQueryAsync(insertTxRow);
                await first.CommitAsync();
                Require(fixture, "session transaction", "CommitAsync closes the transaction.", !first.IsInTransaction);
                var afterCommit = await second.ExecuteQueryAsync(selectTxRow);
                Require(fixture, "session transaction", "another session sees a committed row.", afterCommit.ObjectRows.Count == 1);
            });

            await CheckAsync(fixture, "session transaction", "a typed COMMIT or ROLLBACK ends the session transaction", async () =>
            {
                // The previous check left row 100 committed.
                var cleanup = await first.ExecuteQueryAsync(deleteTxRow);
                Require(fixture, "session transaction", $"the marker row is cleared ({cleanup.Error?.Summary}).", !cleanup.HasError);

                await first.BeginTransactionAsync();
                var insert = await first.ExecuteQueryAsync(insertTxRow);
                Require(fixture, "session transaction", $"the insert runs ({insert.Error?.Summary}).", !insert.HasError);

                var commit = await first.ExecuteQueryAsync("COMMIT;");
                Require(fixture, "session transaction", $"a typed COMMIT runs ({commit.Error?.Summary}).", !commit.HasError);
                Require(fixture, "session transaction", "a typed COMMIT ends the session transaction.", !first.IsInTransaction);
                var committed = await second.ExecuteQueryAsync(selectTxRow);
                Require(fixture, "session transaction", "the typed COMMIT made the row visible to another session.", committed.ObjectRows.Count == 1);

                // The session keeps working: it can start and roll back a new transaction.
                await first.BeginTransactionAsync();
                Require(fixture, "session transaction", "a new transaction can begin after a typed COMMIT.", first.IsInTransaction);
                var delete = await first.ExecuteQueryAsync(deleteTxRow);
                Require(fixture, "session transaction", $"statements run in the new transaction ({delete.Error?.Summary}).", !delete.HasError);
                var rollback = await first.ExecuteQueryAsync("ROLLBACK;");
                Require(fixture, "session transaction", $"a typed ROLLBACK runs ({rollback.Error?.Summary}).", !rollback.HasError);
                Require(fixture, "session transaction", "a typed ROLLBACK ends the session transaction.", !first.IsInTransaction);
                var restored = await second.ExecuteQueryAsync(selectTxRow);
                Require(fixture, "session transaction", "the rolled back delete left the row in place.", restored.ObjectRows.Count == 1);
            });

            await CheckAsync(fixture, "session cancellation", "a cancelled token cancels the run", async () =>
            {
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                var result = await first.ExecuteQueryAsync(
                    "SELECT 1;",
                    new QueryExecutionOptions { CancellationToken = cancellation.Token });
                Require(fixture, "session cancellation", "a cancelled run reports IsCanceled.", result.Error?.IsCanceled == true);

                var after = await first.ExecuteQueryAsync("SELECT 1;");
                Require(fixture, "session cancellation", "the session stays usable after a cancelled run.", !after.HasError);
            });

            await CheckAsync(fixture, "session cancellation", "CancelAsync stops a long-running statement", async () =>
            {
                if (dialect.LongRunningSql is not { } longRunning)
                    return;

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var running = second.ExecuteQueryAsync(longRunning);
                await Task.Delay(TimeSpan.FromMilliseconds(500));
                await second.CancelAsync();
                var finished = await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(15)));
                Require(fixture, "session cancellation",
                    $"the statement stopped within 15s of CancelAsync (still running after {stopwatch.Elapsed.TotalSeconds:F1}s).",
                    ReferenceEquals(finished, running));

                var result = await running;
                Require(fixture, "session cancellation",
                    $"the stopped statement reports it was cancelled, not another error ({result.Error?.Summary}).",
                    result.Error?.IsCanceled == true);

                var after = await second.ExecuteQueryAsync("SELECT 1;");
                Require(fixture, "session cancellation", $"the session stays usable after CancelAsync ({after.Error?.Summary}).", !after.HasError);
            });
        }
        finally
        {
            // A check that failed midway may leave a transaction open or row 100 committed.
            try
            {
                if (first.IsInTransaction)
                    await first.RollbackAsync();
                await first.ExecuteQueryAsync("ROLLBACK;");
                await first.ExecuteQueryAsync($"DELETE FROM {table} WHERE {id} = 100;");
            }
            catch (Exception)
            {
                // Best effort; the fixture drops its table on disposal anyway.
            }
        }
    }

    /// <summary>DDL for a table with an identity/auto-increment PK and a column with a server-side
    /// default, used by the "insert only edited columns" check to prove both apply when untouched.</summary>
    private static string ProviderInsertDefaultsDdl(string providerName, string table, string sequence) => providerName switch
    {
        "SQLite" => $"""
            CREATE TABLE {table} (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NULL,
                amount INTEGER NOT NULL DEFAULT 42
            );
            """,
        "PostgreSQL" => $"""
            CREATE TABLE {table} (
                id INT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                name VARCHAR(100) NULL,
                amount INT NOT NULL DEFAULT 42
            );
            """,
        "SQL Server" => $"""
            CREATE TABLE {table} (
                id INT IDENTITY(1,1) PRIMARY KEY,
                name VARCHAR(100) NULL,
                amount INT NOT NULL DEFAULT 42
            );
            """,
        "MySQL" => $"""
            CREATE TABLE {table} (
                id INT AUTO_INCREMENT PRIMARY KEY,
                name VARCHAR(100) NULL,
                amount INT NOT NULL DEFAULT 42
            );
            """,
        // DuckDB has no AUTOINCREMENT/IDENTITY clause; a sequence-backed default (created separately
        // by the caller) is its equivalent.
        "DuckDB" => $"""
            CREATE TABLE {table} (
                id INTEGER PRIMARY KEY DEFAULT nextval('{sequence}'),
                name VARCHAR NULL,
                amount INTEGER NOT NULL DEFAULT 42
            );
            """,
        _ => throw new NotSupportedException($"No insert-defaults DDL is defined for {providerName}.")
    };

    private static async Task CheckAsync(
        ProviderConformanceFixture fixture,
        string capability,
        string operation,
        Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not XunitException)
        {
            throw new XunitException(Context(fixture, capability, operation, ex.Message));
        }
    }

    private static void Require(
        ProviderConformanceFixture fixture,
        string capability,
        string detail,
        bool condition)
    {
        if (!condition)
            throw new XunitException(Context(fixture, capability, "contract assertion", detail));
    }

    private static void Require(
        IDatabaseProvider provider,
        string capability,
        string detail,
        bool condition)
    {
        if (!condition)
            throw new XunitException($"{provider.ProviderName} [declaration] {capability}: {detail}");
    }

    private static string Context(ProviderConformanceFixture fixture, string capability, string operation, string detail) =>
        $"{fixture.ProviderName} {fixture.DatabaseVersion} [{capability}] {operation}: {detail}";
}

public sealed record ProviderConformanceDescriptor(
    string ProviderName,
    bool HasLocalFixture,
    bool SupportsNativeMultipleResults,
    string ParameterSyntax,
    string ConnectionEnvironmentVariable);
