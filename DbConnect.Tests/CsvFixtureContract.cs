using System.Text.Json;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public static class CsvFixtureContract
{
    public sealed record Fixture(string File, string Delimiter, bool Header, int[] Ordinals, string?[][] Expected, string? Diagnostic);
    public sealed record Manifest(string[] TargetNames, string TargetType, string[] Modes, Fixture[] Fixtures);

    public static async Task RunAsync(DatabaseSession session)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Csv");
        var manifest = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(Path.Combine(directory, "manifest.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        foreach (var fixture in manifest.Fixtures)
            foreach (var mode in manifest.Modes.Select(Enum.Parse<ImportMode>))
            {
                var table = "rain_csv_" + Guid.NewGuid().ToString("N") + ".odd'雨";
                var quote = session.Capabilities.QuoteIdentifier;
                var options = new CsvImportOptions
                {
                    FilePath = Path.Combine(directory, fixture.File),
                    Delimiter = fixture.Delimiter[0],
                    HasHeaderRow = fixture.Header,
                    TableName = table,
                    Mode = mode,
                    TruncateBeforeImport = true,
                    ColumnSpecs = fixture.Ordinals.Select((source, i) => new CsvColumnSpec
                    {
                        SourceOrdinal = source,
                        TargetColumnName = manifest.TargetNames[i],
                        OverrideType = Enum.Parse<DetectedColumnType>(manifest.TargetType)
                    }).ToList()
                };
                var created = false;
                try
                {
                    if (mode == ImportMode.ExistingTable)
                    {
                        await session.ExecuteNonQueryAsync(CsvImportExecution.CreateTableSql(session, table, options.ColumnSpecs));
                        created = true;
                    }
                    var result = await CsvImportService.ExecuteImportAsync(session, options);
                    if (result.RequiredAcknowledgement is { } warning)
                    {
                        options.RiskAcknowledgement = warning.Token;
                        result = await CsvImportService.ExecuteImportAsync(session, options);
                    }
                    if (fixture.Diagnostic != null)
                    {
                        Assert.False(result.Succeeded);
                        Assert.Contains(fixture.Diagnostic, result.ErrorMessage);
                        continue;
                    }
                    Assert.True(result.Succeeded, $"CSV fixture {fixture.File}, {mode}: import failed ({result.Outcome}).");
                    created = true;
                    Assert.Equal(fixture.Expected.Length, result.RowsImported);
                    var stored = await session.ExecuteQueryAsync($"SELECT {quote("select")}, {quote("quote\"bracket]")} FROM {quote(table)}");
                    Assert.False(stored.HasError);
                    var expected = fixture.Expected.Select(r => JsonSerializer.Serialize(r)).Order().ToArray();
                    var actual = stored.ObjectRows.Select(r => JsonSerializer.Serialize(r.Select(v => v?.ToString()).ToArray())).Order().ToArray();
                    Assert.True(expected.SequenceEqual(actual), $"CSV fixture {fixture.File}, {mode}: stored values differ.");
                }
                finally
                {
                    if (created) await session.ExecuteNonQueryAsync($"DROP TABLE {quote(table)}");
                }
            }
    }
}
