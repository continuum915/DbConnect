using System;
using System.IO;

namespace DbConnect.Services;

/// <summary>
/// Process-wide persistence location. Set DBCONNECT_DATA_DIRECTORY in the child process
/// environment before starting DbConnect; the choice is frozen on first use.
/// </summary>
public static class AppDataPaths
{
    private static readonly Lazy<string?> OverrideDirectory = new(ReadOverride);

    public static string RootDirectory => OverrideDirectory.Value ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DbConnect");

    public static string DesktopExportDirectory => OverrideDirectory.Value is { } directory
        ? Path.Combine(directory, "exports")
        : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

    internal static string CredentialDirectory
    {
        get
        {
            if (OverrideDirectory.Value is { } directory)
                return directory;
            if (OperatingSystem.IsWindows())
                return RootDirectory;
            if (OperatingSystem.IsMacOS())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support", "DbConnect");

            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(xdg, "DbConnect");
        }
    }

    private static string? ReadOverride()
    {
        var directory = Environment.GetEnvironmentVariable("DBCONNECT_DATA_DIRECTORY");
        if (directory is null)
            return null;
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new ArgumentException("DBCONNECT_DATA_DIRECTORY must be an absolute directory path.");
        return Path.GetFullPath(directory);
    }
}
