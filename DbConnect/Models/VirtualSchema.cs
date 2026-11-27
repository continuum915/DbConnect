using System.Collections.Generic;

namespace DbConnect.Models;

public class VirtualSchema
{
    public string Name { get; set; } = "Untitled Model";
    public List<VirtualTable> Tables { get; set; } = new();
    public List<VirtualRelationship> Relationships { get; set; } = new();
}

public class VirtualTable
{
    public string Name { get; set; } = "new_table";
    public List<ColumnDefinition> Columns { get; set; } = new();
    /// <summary>Canvas position (same as TableNodeLayout for integration).</summary>
    public double X { get; set; }
    public double Y { get; set; }
}

public class VirtualRelationship
{
    public string Name { get; set; } = string.Empty;
    public string FromTable { get; set; } = string.Empty;
    public string FromColumn { get; set; } = string.Empty;
    public string ToTable { get; set; } = string.Empty;
    public string ToColumn { get; set; } = string.Empty;
}

public class DbConnectModelDocument
{
    public string Version { get; set; } = "1";
    public VirtualSchema Schema { get; set; } = new();
    public DiagramWorkspace Workspace { get; set; } = new();
}
