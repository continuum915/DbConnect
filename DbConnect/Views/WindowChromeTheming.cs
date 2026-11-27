using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;

namespace DbConnect.Views;

/// <summary>
/// Colours the native title bar of every window from the theme: on Windows 11 the caption takes the palette's
/// <c>TitleBar</c> colour and its text the <c>Text</c> colour, and on Windows 10 the caption switches to dark mode in
/// the Dark variant. It re-applies when the window's theme variant changes. Elsewhere, and whenever the platform call
/// fails, it does nothing: the title bar keeps the system look.
/// </summary>
internal static class WindowChromeTheming
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private const int Windows11Build = 22000;

    private static bool _registered;

    /// <summary>Raised for each window the theming is applied to, with the variant used. A seam for tests.</summary>
    internal static event Action<Window, ThemeVariant>? Applied;

    /// <summary>Themes every window when it loads, for the life of the process. Safe to call more than once.</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        Control.LoadedEvent.AddClassHandler<Window>(OnWindowLoaded, RoutingStrategies.Direct);
    }

    private static void OnWindowLoaded(Window window, RoutedEventArgs e)
    {
        Apply(window);
        window.ActualThemeVariantChanged += (_, _) => Apply(window);
    }

    private static void Apply(Window window)
    {
        var variant = window.ActualThemeVariant;
        Applied?.Invoke(window, variant);
        if (!OperatingSystem.IsWindows()) return;
        if (window.TryGetPlatformHandle() is not { HandleDescriptor: "HWND", Handle: var hwnd } || hwnd == IntPtr.Zero) return;

        try
        {
            SetInt(hwnd, DwmwaUseImmersiveDarkMode, variant == ThemeVariant.Dark ? 1 : 0);
            if (Environment.OSVersion.Version.Build < Windows11Build) return;
            if (Colour(window, variant, "DbConnect.Color.TitleBar") is { } caption) SetInt(hwnd, DwmwaCaptionColor, ColorRef(caption));
            if (Colour(window, variant, "DbConnect.Color.Text") is { } text) SetInt(hwnd, DwmwaTextColor, ColorRef(text));
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // Cosmetic only: an older system keeps its own title bar.
        }
    }

    private static Color? Colour(Window window, ThemeVariant variant, string key) =>
        window.TryFindResource(key, variant, out var value) && value is Color colour ? colour : null;

    /// <summary>A Win32 COLORREF: 0x00BBGGRR.</summary>
    private static int ColorRef(Color colour) => colour.R | (colour.G << 8) | (colour.B << 16);

    private static void SetInt(IntPtr hwnd, int attribute, int value) =>
        _ = DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
