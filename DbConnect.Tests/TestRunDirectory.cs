namespace DbConnect.Tests;

/// <summary>Owns only marked directories directly beneath the dedicated test temporary parent.</summary>
internal static class TestRunDirectory
{
    internal static string Parent => Path.Combine(Path.GetTempPath(), "rain-tests");
    internal const string MarkerName = ".dbconnect-test-run";
    private const string MarkerVersion = "rain-test-run-v1";

    internal static string Create()
    {
        Directory.CreateDirectory(Parent);
        if ((File.GetAttributes(Parent) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The test temporary parent must not be a link.");
        var directory = Path.Combine(Parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, MarkerName), MarkerVersion);
        return directory;
    }

    internal static string Validate(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        if (!Path.IsPathFullyQualified(directory) ||
            !string.Equals(Path.GetDirectoryName(fullPath), Path.GetFullPath(Parent),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            !Guid.TryParseExact(Path.GetFileName(fullPath), "N", out _) ||
            (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(Parent) & FileAttributes.ReparsePoint) != 0 ||
            File.ReadAllText(Path.Combine(fullPath, MarkerName)) != MarkerVersion)
            throw new InvalidOperationException("Refusing an unowned test-run directory.");
        return fullPath;
    }

    internal static void Delete(string directory)
    {
        var fullPath = Validate(directory);
        var pending = new Stack<string>();
        pending.Push(fullPath);
        while (pending.TryPop(out var current))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Refusing to clean a test directory containing a link.");
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
            }
        }
        Directory.Delete(fullPath, recursive: true);
    }
}
