using System.Collections.Generic;

namespace DbConnect.Models;

public class AppSettings
{
    public string ThemeVariant { get; set; } = "Light";
    public double WindowWidth { get; set; } = 1200;
    public double WindowHeight { get; set; } = 800;
    public double WindowX { get; set; } = double.NaN;
    public double WindowY { get; set; } = double.NaN;
    public double SplitterWidth { get; set; } = 250;
    public List<string> RecentConnectionNames { get; set; } = new();

    /// <summary>Recently opened or saved SQL files, most recent first.</summary>
    public List<string> RecentFiles { get; set; } = new();
    public ExportOptions LastExportOptions { get; set; } = new();

    /// <summary>URL returning {"version":"x.y.z","releaseUrl":"..."} for update checks. Empty = disabled.</summary>
    public string UpdateCheckUrl { get; set; } = "";

    /// <summary>Minimum query duration in milliseconds before a query is logged as slow.</summary>
    public int SlowQueryThresholdMs { get; set; } = 1000;

    public bool AuditLogEnabled { get; set; } = true;

    public bool SidebarCollapsed { get; set; } = false;

    /// <summary>Height of the sidebar's Properties/Snippets region under the tree.</summary>
    public double SidebarLowerHeight { get; set; } = 280;
    public bool SidebarSnippetsExpanded { get; set; } = true;

    /// <summary>The right-hand Information pane: shown, and its width.</summary>
    public bool InformationPaneVisible { get; set; } = true;
    public double InformationPaneWidth { get; set; } = 260;

    /// <summary>When true, restore the previous session's tabs on startup (default enabled).</summary>
    public bool RestorePreviousSession { get; set; } = true;

    public EditorPreferences Editor { get; set; } = new();
    public ResultPreferences Results { get; set; } = new();
    public ExecutionPreferences Execution { get; set; } = new();
}
