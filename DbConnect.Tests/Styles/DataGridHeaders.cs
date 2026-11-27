using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace DbConnect.Tests.Styles;

/// <summary>Finds a grid's column header texts and tells whether one is cut short.</summary>
internal static class DataGridHeaders
{
    /// <summary>The text of each column header under <paramref name="root"/> that has any.</summary>
    public static IEnumerable<TextBlock> Texts(Visual root) =>
        root.GetVisualDescendants().OfType<DataGridColumnHeader>()
            .Select(header => header.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => !string.IsNullOrEmpty(t.Text)))
            .OfType<TextBlock>();

    /// <summary>True when the header's text needs more width than it was given.</summary>
    public static bool IsTrimmed(TextBlock header) => NaturalWidth(header) > header.Bounds.Width + 0.5;

    private static double NaturalWidth(TextBlock text)
    {
        var probe = new TextBlock
        {
            Text = text.Text,
            FontFamily = text.FontFamily,
            FontSize = text.FontSize,
            FontWeight = text.FontWeight,
        };
        probe.Measure(Size.Infinity);
        return probe.DesiredSize.Width;
    }
}
