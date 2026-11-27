using System.Collections.Generic;

namespace DbConnect.Models;

public class DiagramWorkspace
{
    public string ConnectionName { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public string SavedAt { get; set; } = string.Empty;
    public List<TableNodeLayout> TableLayouts { get; set; } = new();
    public List<string> HiddenTables { get; set; } = new();
    public List<DiagramNote> Notes { get; set; } = new();
}

public class TableNodeLayout
{
    public string TableName { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public string? ColorHex { get; set; }
    public string? ColorGroup { get; set; }
}

public class DiagramNote
{
    public string Text { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 200;
}
