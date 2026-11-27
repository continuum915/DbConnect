using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace DbConnect.Tests.Styles;

/// <summary>Shows content in a window with the app's styles in Light or Dark and resolves theme resources in it.</summary>
/// <remarks>Theories take the variant as a string so xUnit lists the Light and Dark cases separately.</remarks>
internal sealed class ThemedHost : IDisposable
{
    public static TheoryData<string> Variants => new() { "Light", "Dark" };

    private ThemedHost(Window window) => Window = window;

    public Window Window { get; }

    public static ThemedHost Show(Control content, string variant, double height = 400, double width = 600)
    {
        var window = new Window
        {
            Width = width,
            Height = height,
            Content = content,
            RequestedThemeVariant = VariantOf(variant),
        };
        window.Show();
        window.UpdateLayout();
        return new ThemedHost(window);
    }

    public static ThemeVariant VariantOf(string variant) => variant == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

    public bool Has(string key) => Window.TryFindResource(key, Window.ActualThemeVariant, out _);

    public object Resource(string key)
    {
        Assert.True(Window.TryFindResource(key, Window.ActualThemeVariant, out var value), $"{key} not found");
        return value!;
    }

    public Color Brush(string key) => ColorOf((IBrush)Resource(key));

    public static Color ColorOf(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    public void Dispose() => Window.Close();
}
