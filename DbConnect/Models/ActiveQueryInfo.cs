using System;

namespace DbConnect.Models;

public class ActiveQueryInfo
{
    public string ConnectionId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Database { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public string QueryText { get; set; } = string.Empty;
    public bool IsBlocking { get; set; }
}
