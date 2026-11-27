namespace DbConnect.Tests.Architecture;

/// <summary>
/// Views still holding FontSize, colour or off-scale spacing literals, waiting for their ticket in the UI
/// consistency pass. Entries may only be removed, never added: once a view is
/// migrated, take it off this list in the same change.
/// </summary>
internal static class ViewStylingBudgets
{
    public static readonly HashSet<string> Unmigrated = new(StringComparer.Ordinal)
    {
    };

    /// <summary>
    /// Windows without a default/cancel button pair, and why (ticket 07, D1). Read by the dialog fitness test and
    /// the headless keyboard test. Entries may only be removed, never added.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> WindowsWithoutDialogKeys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["MainWindow.axaml"] = "the application window, not a dialog",
        ["CommandPaletteView.axaml"] = "Enter runs the highlighted command and Esc closes, from code-behind",
        ["GlobalSearchOverlay.axaml"] = "Enter opens the highlighted result and Esc closes, from code-behind",
    };
}
