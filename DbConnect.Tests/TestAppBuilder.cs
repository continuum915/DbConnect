using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;

[assembly: AvaloniaTestApplication(typeof(DbConnect.Tests.TestAppBuilder))]

namespace DbConnect.Tests;

/// <summary>
/// One-time headless bootstrap for <see cref="AvaloniaFactAttribute"/> tests. Uses the real
/// <see cref="DbConnect.App"/> (not a bare Application) so styles/resources (FluentTheme, the DataGrid
/// theme, DbConnectTheme) are loaded exactly as they are for the real app, and a rendered control's
/// state reflects what a user would actually see.
///
/// <see cref="Avalonia.AppBuilderBase{TAppBuilder}.WithInterFont"/> matches the real app's
/// Program.cs, registering Inter as the default UI font.
///
/// Rendering goes through Skia (<c>UseHeadlessDrawing = false</c>) rather than Avalonia.Headless's
/// drawing stub, whose font stub can't resolve custom embedded fonts such as FluentIcons.Avalonia's
/// icon font. With Skia, a View whose toolbar or popups draw those glyphs can be shown in a real
/// Window, so its DataGrid realizes rows and cells a test can inspect.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        TestDataBootstrap.EnsureInitialized();
        return AppBuilder.Configure<DbConnect.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();
    }
}
