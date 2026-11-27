using Avalonia.Media;

namespace DbConnect.Tests.Styles;

/// <summary>WCAG 2.x contrast ratio between two colours, over resolved brushes.</summary>
internal static class Contrast
{
    public const double MinimumForText = 4.5;

    public static double Ratio(Color a, Color b)
    {
        var (lighter, darker) = (Luminance(a), Luminance(b)) is var (x, y) && x > y ? (x, y) : (y, x);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>Colours a brush paints: its colour, or every stop of a gradient.</summary>
    public static IReadOnlyList<Color> ColoursOf(IBrush? brush) => brush switch
    {
        ISolidColorBrush solid => [solid.Color],
        IGradientBrush gradient => gradient.GradientStops.Select(stop => stop.Color).ToList(),
        _ => throw new InvalidOperationException($"Cannot measure contrast against {brush?.GetType().Name ?? "null"}."),
    };

    /// <summary>Asserts text is readable on every colour the background paints.</summary>
    public static void AssertReadable(IBrush? text, IBrush? background, string what)
    {
        var foreground = Assert.Single(ColoursOf(text));
        foreach (var colour in ColoursOf(background))
        {
            var ratio = Ratio(foreground, colour);
            Assert.True(ratio >= MinimumForText, $"{what}: {foreground} on {colour} is {ratio:0.00}:1, below {MinimumForText}:1");
        }
    }

    private static double Luminance(Color colour) =>
        (0.2126 * Channel(colour.R)) + (0.7152 * Channel(colour.G)) + (0.0722 * Channel(colour.B));

    private static double Channel(byte value)
    {
        var c = value / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
