using System;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// The preferences in effect, readable from views and formatters without plumbing. Updated when
/// settings load and when the Preferences dialog applies. The values are copies; edit settings and
/// call <see cref="Apply"/> to change them. Tests that change these run in the non-parallel
/// "AppPreferences" collection.
/// </summary>
public static class AppPreferences
{
    public static EditorPreferences Editor { get; private set; } = new();
    public static ResultPreferences Results { get; private set; } = new();
    public static ExecutionPreferences Execution { get; private set; } = new();

    /// <summary>Raised once per <see cref="Apply"/>, on the caller's thread.</summary>
    public static event Action? Changed;

    public static void Apply(AppSettings settings)
    {
        Editor = (settings.Editor ?? new()) with { };
        Results = (settings.Results ?? new()) with { };
        Execution = (settings.Execution ?? new()) with { ConfirmDataChangesOnTiers = [.. settings.Execution?.ConfirmDataChangesOnTiers ?? []] };
        Changed?.Invoke();
    }

    /// <summary>Restores defaults; for tests.</summary>
    internal static void Reset() => Apply(new AppSettings());
}
