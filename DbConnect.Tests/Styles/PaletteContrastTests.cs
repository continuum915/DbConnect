using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;

namespace DbConnect.Tests.Styles;

/// <summary>
/// UI redesign 02: every text colour the design uses stays readable on every surface and row state it is drawn on, in
/// Light and Dark. Shades the design gave that fell short are adjusted in DbConnectTheme's Colors.axaml (with a note there).
/// </summary>
public sealed class PaletteContrastTests
{
    private static readonly string[] TextRoles = ["Text", "TextSecondary", "TextMuted", "TextNull"];

    private static readonly string[] Backgrounds =
        ["Surface", "Panel", "Toolbar", "GridHeader", "Selection", "CurrentRow", "DirtyBg", "NewRowBg", "RunBg"];

    public static TheoryData<string, string, string> Pairs()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var variant in ThemedHost.Variants)
        {
            foreach (var text in TextRoles)
            {
                foreach (var background in Backgrounds)
                    data.Add(variant, text, background);
            }
        }

        return data;
    }

    [AvaloniaTheory]
    [MemberData(nameof(Pairs))]
    public void TextRole_IsReadableOnBackgroundRole(string variant, string text, string background)
    {
        using var host = ThemedHost.Show(new Border(), variant);

        var foreground = (Color)host.Resource($"DbConnect.Color.{text}");
        var surface = (Color)host.Resource($"DbConnect.Color.{background}");
        var ratio = Contrast.Ratio(foreground, surface);
        Assert.True(ratio >= Contrast.MinimumForText, $"{text} on {background} ({variant}) is {ratio:0.00}:1");
    }
}
