using System;
using System.Collections.Generic;

namespace DbConnect.Models;

public class TabSnapshot
{
    public string Title { get; set; } = string.Empty;
    public string QueryText { get; set; } = string.Empty;
    public string? SessionName { get; set; }
    public Guid? ConnectionId { get; set; }
    /// <summary>The file the tab was bound to; null for an untitled tab.</summary>
    public string? FilePath { get; set; }
    /// <summary>Whether the tab had unsaved changes when the snapshot was taken.</summary>
    public bool IsDirty { get; set; }

    public bool MatchesConnection(ConnectionInfo connection) => ConnectionId.HasValue
        ? ConnectionId.Value == connection.Id
        : SessionName == connection.Name;
}

public class SessionSnapshot
{
    public int Pid { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public List<TabSnapshot> Tabs { get; set; } = new();
    public string? ActiveTabTitle { get; set; }
}
