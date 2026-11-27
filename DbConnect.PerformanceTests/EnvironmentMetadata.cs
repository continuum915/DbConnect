using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DbConnect.PerformanceTests;

public sealed record EnvironmentMetadata(string Machine, string Os, string Runtime, string Architecture,
    int ProcessorCount, string BuildConfiguration, string Commit, string Dirty, Dictionary<string, string> Packages,
    string SourceIdentity = "Embedded at build time from the runner project checkout")
{
    public static async Task<EnvironmentMetadata> CaptureAsync()
    {
        var assembly = typeof(EnvironmentMetadata).Assembly;
        var identity = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value);
        var packages = new Dictionary<string, string>();
        var deps = Path.ChangeExtension(assembly.Location, ".deps.json");
        if (File.Exists(deps))
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(deps));
            foreach (var library in document.RootElement.GetProperty("libraries").EnumerateObject())
            {
                if (library.Value.GetProperty("type").GetString() == "package")
                {
                    var separator = library.Name.LastIndexOf('/');
                    packages[library.Name[..separator]] = library.Name[(separator + 1)..];
                }
            }
        }
        return new(Environment.MachineName, RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription,
            RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount,
#if DEBUG
            "Debug",
#else
            "Release",
#endif
            identity.GetValueOrDefault("PerformanceBuildCommit") ?? "unavailable",
            identity.GetValueOrDefault("PerformanceBuildDirty") ?? "unavailable", packages);
    }
}
