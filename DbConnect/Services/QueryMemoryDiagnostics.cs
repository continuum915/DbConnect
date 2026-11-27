using System;
using System.Diagnostics;

namespace DbConnect.Services;

/// <summary>Optional coarse memory boundaries; no per-statement events or SQL content.</summary>
internal static class QueryMemoryDiagnostics
{
    internal const string SourceName = "DbConnect.QueryMemory";
    private static readonly ActivitySource Source = new(SourceName);
    internal static Activity? Begin(string stage) => Source.StartActivity(stage);

    internal static T Measure<T>(string stage, Func<T> action)
    {
        using var diagnostic = Begin(stage);
        return action();
    }
}
