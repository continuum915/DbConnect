using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbConnect.PerformanceTests;
using PerformanceProgram = DbConnect.PerformanceTests.Program;

namespace DbConnect.Tests;

public sealed class EditorScriptGenerationTests
{
    [Fact]
    public async Task ScriptCommandWritesExactSizedReproducibleFilesAndAHashManifest()
    {
        var output = Path.Combine(Path.GetTempPath(), "rain-editor-scripts-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(0, await PerformanceProgram.Main(["--generate-scripts", output, "--sizes-kib", "8", "--profiles", "representative,long-lines"]));
            var manifestText = await File.ReadAllTextAsync(Path.Combine(output, "manifest.json"));
            using var manifest = JsonDocument.Parse(manifestText);
            var files = manifest.RootElement.GetProperty("Files").EnumerateArray().ToArray();
            Assert.Equal(2, files.Length);
            foreach (var file in files)
            {
                var bytes = await File.ReadAllBytesAsync(Path.Combine(output, file.GetProperty("FileName").GetString()!));
                Assert.Equal(8192, bytes.Length);
                Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), file.GetProperty("Sha256").GetString());
                var sql = Encoding.UTF8.GetString(bytes);
                Assert.StartsWith("-- GO", sql);
                Assert.EndsWith("*/\r\n", sql);
                Assert.DoesNotContain("\n", sql.Replace("\r\n", ""));
            }
            Assert.Equal(0, await PerformanceProgram.Main(["--generate-scripts", output, "--sizes-kib", "8", "--profiles", "representative,long-lines"]));
            Assert.Equal(manifestText, await File.ReadAllTextAsync(Path.Combine(output, "manifest.json")));
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, true); }
    }
}
