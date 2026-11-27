namespace DbConnect.Tests;

/// <summary>Paths in the source tree, for tests that scan source files rather than build output.</summary>
internal static class TestRepo
{
    /// <summary>The directory holding DbConnect.slnx, found by walking up from the test output directory.</summary>
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DbConnect.slnx")))
                return directory.FullName;
        }

        throw new InvalidOperationException("DbConnect.slnx was not found above the test output directory.");
    }
}
