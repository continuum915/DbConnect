using System;
using System.Collections.Generic;

namespace DbConnect.Models;

public class SavedTab
{
    public string TabType { get; set; } = "Query"; // "Query" | "TableData"
    public string Title { get; set; } = string.Empty;
    public string? ConnectionName { get; set; }
    public Guid? ConnectionId { get; set; }
    public string? QueryText { get; set; }
    public string? TableName { get; set; }
    public DatabaseObjectIdentity? Object { get; set; }
    public string? DatabaseName { get; set; }
    public bool IsPinned { get; set; }
    /// <summary>The file a query tab was bound to; null for an untitled tab.</summary>
    public string? FilePath { get; set; }
    /// <summary>Whether a query tab had unsaved changes when the session was saved.</summary>
    public bool IsDirty { get; set; }

    public bool MatchesConnection(ConnectionInfo connection) => ConnectionId.HasValue
        ? ConnectionId.Value == connection.Id
        : ConnectionName == connection.Name;
}

public class SavedSession
{
    public List<SavedTab> Tabs { get; set; } = new();
    public int SelectedTabIndex { get; set; }
    public string SavedAt { get; set; } = string.Empty;
    public bool WasCleanShutdown { get; set; }
}
