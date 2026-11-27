using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace DbConnect.Services;

/// <summary>Opt-in setup boundaries only; never records SQL or user content.</summary>
internal static class EditorSetupDiagnostics
{
    internal const string SourceName = "DbConnect.EditorSetup";
    private static readonly ActivitySource Source = new(SourceName);

    internal static Activity? Begin(string stage) => Source.StartActivity(stage);

    internal static T Measure<T>(string stage, Func<T> action)
    {
        using var activity = Begin(stage);
        return action();
    }

    internal static void Measure(string stage, Action action)
    {
        using var activity = Begin(stage);
        action();
    }

    internal static async Task MeasureAsync(string stage, Func<Task> action)
    {
        using var activity = Begin(stage);
        await action();
    }
}
