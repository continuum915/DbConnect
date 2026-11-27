using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace DbConnect.Tests;

/// <summary>
/// Proves the headless seam end to end: a real Control is instantiated, laid out by Avalonia's
/// headless platform (no display server), and its post-layout, rendered state is inspectable.
/// Future rendering tests (e.g. asserting on a DataGrid's generated columns) build on this.
/// </summary>
public class HeadlessSmokeTests
{
    [AvaloniaFact]
    public void Control_RendersAndLaysOutHeadlessly()
    {
        var textBlock = new TextBlock { Text = "hello" };
        var window = new Window { Content = textBlock };

        try
        {
            window.Show();

            Assert.Equal("hello", textBlock.Text);
            Assert.True(textBlock.Bounds.Width > 0, "layout should have measured/arranged the control.");
        }
        finally
        {
            window.Close();
        }
    }
}
