namespace DbConnect.Models;

/// <summary>
/// RAIN-P2-12 — A commit found <paramref name="Row"/> changed underneath the user; the view asks
/// whether to reload the row or overwrite. <paramref name="CanOverwrite"/> is false when the row
/// can no longer be found, so there is nothing left to overwrite.
/// </summary>
public sealed record ConcurrencyConflictRequest(EditableRow Row, string Message, bool CanOverwrite);

/// <summary>RAIN-P2-12 — How the user resolved a <see cref="ConcurrencyConflictRequest"/>.</summary>
public enum ConcurrencyConflictChoice
{
    ReloadRow,
    Overwrite
}
