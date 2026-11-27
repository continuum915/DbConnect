using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

namespace DbConnect.Tests;

/// <summary>Lets a headless window settle: runs queued UI work, lays out and renders a few frames.</summary>
internal static class HeadlessFrames
{
    public static void Pump(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}
