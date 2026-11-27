using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public class CsvColumnIdentityTests
{
    [Theory]
    [InlineData(false, "first,second,third", 1, "second")]
    [InlineData(true, "same,same,\nfirst,second,third", 0, "first")]
    [InlineData(true, "same,same,\nfirst,second,third", 1, "second")]
    [InlineData(true, "same,same,\nfirst,second,third", 2, "third")]
    public async Task WizardSelectionPreservesSourcePosition(bool header, string content, int selected, string expected)
    {
        await WithDatabase(content, async (path, session, provider, connectionString) =>
        {
            var specs = CsvImportService.DetectColumnTypes(await CsvImportService.ParsePreviewAsync(path, ',', header));
            for (int i = 0; i < specs.Count; i++)
            {
                specs[i].Include = i == selected;
                specs[i].TargetColumnName = "renamed";
            }
            var result = await CsvImportService.ExecuteImportAsync(session, new CsvImportOptions
            {
                FilePath = path,
                HasHeaderRow = header,
                TableName = "imported",
                ColumnSpecs = specs
            });
            Assert.True(result.Succeeded, result.ErrorMessage);
            var rows = await provider.ExecuteQueryAsync(connectionString, "SELECT renamed FROM imported");
            Assert.Equal(new object?[] { expected }, Assert.Single(rows.ObjectRows));
        });
    }

    [Theory]
    [InlineData(true, "left,right\nfirst,second", "right", null, "second")]
    [InlineData(true, "left,right\nfirst,second", "missing", 1, "second")]
    [InlineData(false, "first,second", "ignored", null, "first")]
    public async Task CompatibilityMappingsAndExplicitOrdinals(bool header, string content, string label, int? ordinal, string expected)
    {
        await WithDatabase(content, async (path, session, provider, connectionString) =>
        {
            var result = await CsvImportService.ExecuteImportAsync(session, new CsvImportOptions
            {
                FilePath = path,
                HasHeaderRow = header,
                TableName = "imported",
                ColumnSpecs = [new() { CsvHeader = label, SourceOrdinal = ordinal, TargetColumnName = "target" }]
            });
            Assert.True(result.Succeeded, result.ErrorMessage);
            var rows = await provider.ExecuteQueryAsync(connectionString, "SELECT target FROM imported");
            Assert.Equal(new object?[] { expected }, Assert.Single(rows.ObjectRows));
        });
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("ambiguous")]
    [InlineData("negative")]
    [InlineData("outside")]
    [InlineData("excluded")]
    [InlineData("blank")]
    [InlineData("nul")]
    [InlineData("duplicate")]
    [InlineData("names-shape")]
    [InlineData("names-blank")]
    [InlineData("names-duplicate")]
    [InlineData("inferred-duplicate")]
    public async Task InvalidMappingsFailBeforeCreatingOrReplacingTables(string invalid)
    {
        await WithDatabase("same,same\nfirst,second", async (path, session, provider, connectionString) =>
        {
            await session.ExecuteNonQueryBatchAsync("CREATE TABLE existing (target TEXT); INSERT INTO existing VALUES ('preserved');");
            foreach (var mode in new[] { ImportMode.ExistingTable, ImportMode.NewTable })
            {
                var options = new CsvImportOptions
                {
                    FilePath = path,
                    TableName = mode == ImportMode.NewTable ? "new_table" : "existing",
                    Mode = mode,
                    TruncateBeforeImport = true,
                    ColumnSpecs = [new() { SourceOrdinal = 0, TargetColumnName = "target" }]
                };
                var spec = options.ColumnSpecs[0];
                switch (invalid)
                {
                    case "missing": spec.SourceOrdinal = null; spec.CsvHeader = "missing"; break;
                    case "ambiguous": spec.SourceOrdinal = null; spec.CsvHeader = "same"; break;
                    case "negative": spec.SourceOrdinal = -1; break;
                    case "outside": spec.SourceOrdinal = 2; break;
                    case "excluded": spec.Include = false; break;
                    case "blank": spec.TargetColumnName = " "; break;
                    case "nul": spec.TargetColumnName = "bad\0name"; break;
                    case "duplicate": options.ColumnSpecs.Add(new() { SourceOrdinal = 1, TargetColumnName = "TARGET" }); break;
                    default:
                        options.ColumnSpecs.Clear();
                        options.ColumnNames = invalid switch
                        {
                            "names-shape" => ["target"],
                            "names-blank" => ["target", ""],
                            "names-duplicate" => ["target", "TARGET"],
                            _ => []
                        };
                        break;
                }
                var result = await CsvImportService.ExecuteImportAsync(session, options);
                Assert.False(result.Succeeded);
                Assert.Equal(0, result.RowsImported);
                Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
            }
            var rows = await provider.ExecuteQueryAsync(connectionString, "SELECT target FROM existing");
            Assert.Equal(new object?[] { "preserved" }, Assert.Single(rows.ObjectRows));
            var created = await provider.ExecuteQueryAsync(connectionString, "SELECT name FROM sqlite_master WHERE name = 'new_table'");
            Assert.Empty(created.ObjectRows);
        });
    }

    private static async Task WithDatabase(string content, Func<string, DatabaseSession, SqliteProvider, string, Task> test)
    {
        var path = Path.GetTempFileName();
        var connectionString = $"Data Source=identity_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var keepAlive = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await keepAlive.OpenAsync();
        try
        {
            await File.WriteAllTextAsync(path, content);
            var provider = new SqliteProvider();
            var session = new DatabaseSession(new ConnectionInfo
            {
                Name = "csv",
                Provider = "SQLite",
                ConnectionString = connectionString
            }, provider);
            await test(path, session, provider, connectionString);
        }
        finally { File.Delete(path); }
    }
}
