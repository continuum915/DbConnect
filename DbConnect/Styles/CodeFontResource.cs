using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using DbConnect.Services;

namespace DbConnect.Styles;

/// <summary>Keeps the <c>DbConnect.FontFamily.Code</c> resource, used by the <c>code</c> style classes, on the editor font from Preferences.</summary>
internal static class CodeFontResource
{
    public const string Key = "DbConnect.FontFamily.Code";

    private static Application? _application;

    /// <summary>Applies the font to <paramref name="application"/> now and on every Preferences change. One handler serves the latest application.</summary>
    public static void Attach(Application application)
    {
        if (_application == null) AppPreferences.Changed += OnPreferencesChanged;
        _application = application;
        Apply();
    }

    private static void OnPreferencesChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    private static void Apply()
    {
        if (_application != null) _application.Resources[Key] = new FontFamily(AppPreferences.Editor.FontFamily);
    }
}
