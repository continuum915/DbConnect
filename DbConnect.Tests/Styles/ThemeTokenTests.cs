using System.Text.RegularExpressions;

namespace DbConnect.Tests.Styles;

/// <summary>
/// Ticket 03 (K2), UI redesign 01: DbConnectTheme's control styles colour themselves from the palette tokens, so Dark can
/// restyle them, and the app has left Win7AeroTheme behind.
/// </summary>
public sealed partial class ThemeTokenTests
{
    private static readonly string ThemeDirectory = Path.Combine(TestRepo.Root, "Theme", "DbConnectTheme", "Themes");

    [Fact]
    public void ThemeControlStyles_UseNoHexColourLiterals()
    {
        var literals = Directory.GetFiles(Path.Combine(ThemeDirectory, "Controls"), "*.axaml")
            .SelectMany(file => HexColour().Matches(File.ReadAllText(file)).Select(match => $"{Path.GetFileName(file)}: {match.Value}"))
            .ToList();

        Assert.True(literals.Count == 0, "Use a DbConnect.Brush token instead of:\n" + string.Join("\n", literals));
    }

    [Fact]
    public void Theme_DrawsNoGradients()
    {
        var gradients = Directory.GetFiles(ThemeDirectory, "*.axaml", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("GradientBrush", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(gradients.Count == 0, "The design is flat; remove the gradients in:\n" + string.Join("\n", gradients));
    }

    /// <summary>DbConnect no longer uses Win7AeroTheme; its keys must not creep back into the app or its tests.</summary>
    [Fact]
    public void AppAndTests_ReferenceNoWin7Keys()
    {
        var needle = "Win7" + ".";
        var self = Path.GetFullPath(Path.Combine(TestRepo.Root, "DbConnect.Tests", "Styles", nameof(ThemeTokenTests) + ".cs"));
        var offenders = new[] { "DbConnect", "DbConnect.Tests" }
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(TestRepo.Root, project), "*.*", SearchOption.AllDirectories))
            .Where(file => file.EndsWith(".axaml", StringComparison.Ordinal) || file.EndsWith(".cs", StringComparison.Ordinal))
            .Where(file => !IsBuildOutput(file) && !string.Equals(Path.GetFullPath(file), self, StringComparison.OrdinalIgnoreCase))
            .Where(file => File.ReadAllText(file).Contains(needle, StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(TestRepo.Root, file))
            .ToList();

        Assert.True(offenders.Count == 0, "Use the DbConnect.* key instead of the Win7.* one in:\n" + string.Join("\n", offenders));
    }

    private static bool IsBuildOutput(string file)
    {
        var separator = Path.DirectorySeparatorChar;
        return file.Contains($"{separator}bin{separator}", StringComparison.Ordinal)
            || file.Contains($"{separator}obj{separator}", StringComparison.Ordinal);
    }

    [GeneratedRegex("\"#[0-9A-Fa-f]{3,8}\"")]
    private static partial Regex HexColour();
}
