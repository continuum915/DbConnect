using System.Collections.Generic;

namespace DbConnect.Models;

/// <summary>Query editor preferences. Defaults match the editor's behavior before preferences existed.</summary>
public sealed record EditorPreferences
{
    public string FontFamily { get; set; } = "Consolas,Monospace";
    public int FontSize { get; set; } = 14;
    public int TabWidth { get; set; } = 4;
    public bool InsertSpaces { get; set; }
    public bool WordWrap { get; set; }
    /// <summary>Open completion while typing; Ctrl+Space always opens it.</summary>
    public bool AutoOpenCompletion { get; set; } = true;
}

public enum BinaryDisplayMode
{
    /// <summary>"&lt;N bytes&gt;".</summary>
    Size,
    /// <summary>"0x" followed by the first bytes in hex.</summary>
    Hex
}

/// <summary>How result grids show values. Copy, export and SQL literals are not affected.</summary>
public sealed record ResultPreferences
{
    public string NullText { get; set; } = "NULL";
    public string DateTimeFormat { get; set; } = "yyyy-MM-dd HH:mm:ss";
    public BinaryDisplayMode BinaryDisplay { get; set; } = BinaryDisplayMode.Size;

    /// <summary>Most rows a query result keeps when fetching past the row cap; beyond it, export instead.</summary>
    public int MaxResultRows { get; set; } = 1_000_000;
}

/// <summary>Defaults for running SQL. The default timeout and row cap live in the query workspace store.</summary>
public sealed record ExecutionPreferences
{
    /// <summary>New query tabs start in manual-commit mode.</summary>
    public bool NewTabsManualCommit { get; set; }

    /// <summary>Environment tiers on which running SQL that changes data asks first.</summary>
    public List<string> ConfirmDataChangesOnTiers { get; set; } = ["Production"];
}
