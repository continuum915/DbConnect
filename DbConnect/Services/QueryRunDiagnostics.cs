using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace DbConnect.Services;

/// <summary>Optional stage timings; no SQL, values, or connection details are emitted.</summary>
internal static class QueryRunDiagnostics
{
    internal const string SourceName = "DbConnect.QueryRun";
    private static readonly ActivitySource Source = new(SourceName);

    internal static T Measure<T>(string stage, Func<T> action)
    {
        using var activity = Source.StartActivity(stage);
        return action();
    }

    internal static void Measure(string stage, Action action)
    {
        using var activity = Source.StartActivity(stage);
        action();
    }

    internal static async Task<T> MeasureAsync<T>(string stage, Func<Task<T>> action)
    {
        using var activity = Source.StartActivity(stage);
        return await action();
    }

    internal static async Task MeasureAsync(string stage, Func<Task> action)
    {
        using var activity = Source.StartActivity(stage);
        await action();
    }
}
