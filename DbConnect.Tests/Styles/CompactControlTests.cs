using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace DbConnect.Tests.Styles;

/// <summary>
/// Compact density (ticket 07, D4/D6): check boxes, radio buttons and expander headers are one control row tall.
/// The styles in DbConnect/Styles/Global.axaml reach into Fluent's control templates, so a theme upgrade that renames or
/// reorders template parts would silently undo them; these heights catch that.
/// </summary>
public sealed class CompactControlTests
{
    private const double ControlRow = 24;   // DbConnect.Size.ControlMinHeight
    private const double ExpanderBorder = 2; // the header's 1 px top and bottom border

    [AvaloniaTheory]
    [MemberData(nameof(ThemedHost.Variants), MemberType = typeof(ThemedHost))]
    public void CheckBoxesAndRadioButtons_AreOneControlRowTall(string variant)
    {
        var checkBox = new CheckBox { Content = "Word wrap" };
        var radio = new RadioButton { Content = "Tree" };
        using var host = ThemedHost.Show(new StackPanel { Children = { checkBox, radio } }, variant);

        Assert.Equal(ControlRow, checkBox.Bounds.Height);
        Assert.Equal(ControlRow, radio.Bounds.Height);
    }

    [AvaloniaTheory]
    [MemberData(nameof(ThemedHost.Variants), MemberType = typeof(ThemedHost))]
    public void ExpanderHeaders_AreOneControlRowTall(string variant)
    {
        var expander = new Expander { Header = "Options" };
        using var host = ThemedHost.Show(new StackPanel { Children = { expander } }, variant);

        Assert.Equal(ControlRow + ExpanderBorder, expander.Bounds.Height);
    }
}
