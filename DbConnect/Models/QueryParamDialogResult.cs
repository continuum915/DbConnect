using System.Collections.Generic;

namespace DbConnect.Models;

public class QueryParamDialogResult
{
    public bool Confirmed { get; set; }
    public List<QueryParamEntry> Parameters { get; set; } = new();
    public bool SaveAsSnippet { get; set; }
    public bool SaveParamContext { get; set; }
    public string? ContextName { get; set; }
}
