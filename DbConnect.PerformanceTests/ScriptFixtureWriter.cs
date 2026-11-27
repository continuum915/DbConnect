using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DbConnect.PerformanceTests;

public static class ScriptFixtureWriter
{
    public static async Task RunAsync(string[] args)
    {
        if (args.Length < 2 || args.Length % 2 != 0) throw new ArgumentException("Script options require value pairs.");
        var output = Path.GetFullPath(args[1]);
        int[] sizes = [64, 1024, 16384, 65536];
        string[] profiles = ["representative", "many-small-batches", "long-lines"];
        for (var i = 2; i < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--sizes-kib": sizes = args[i + 1].Split(',').Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray(); break;
                case "--profiles": profiles = args[i + 1].Split(','); break;
                default: throw new ArgumentException($"Unknown script option: {args[i]}");
            }
        }
        if (sizes.Any(size => size < 8 || size > 65536) || profiles.Any(profile =>
            profile is not ("representative" or "many-small-batches" or "long-lines")))
            throw new ArgumentException("Editor fixtures support 8–65536 KiB and representative/many-small-batches/long-lines profiles.");
        Directory.CreateDirectory(output);
        var files = new List<object>();
        foreach (var size in sizes.Distinct().Order())
        {
            foreach (var profile in profiles.Distinct())
            {
                var fixture = RepresentativeImport.Generate(checked(size * 1024), profile);
                var fileName = $"{profile}-{size}KiB.sql";
                await File.WriteAllTextAsync(Path.Combine(output, fileName), fixture.Sql, new UTF8Encoding(false));
                var metadata = fixture.Metadata;
                files.Add(new
                {
                    FileName = fileName,
                    metadata.GeneratorVersion,
                    metadata.Seed,
                    metadata.Profile,
                    metadata.Sha256,
                    metadata.EncodedBytes,
                    metadata.Utf16Characters,
                    metadata.Lines,
                    metadata.GoDirectives,
                    metadata.Inserts,
                    metadata.Tables,
                    metadata.MaximumLineLength,
                    metadata.Configuration
                });
            }
        }
        // Exclude clock/machine fields so regeneration produces an identical manifest.
        var manifest = new { SchemaVersion = 1, Encoding = "UTF-8 without BOM", Newlines = "CRLF", Dialect = "SQL Server", Files = files };
        await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(manifest,
            new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
        Console.WriteLine(output);
    }
}
