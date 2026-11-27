using System.Collections.Generic;

namespace DbConnect.Models;

public class ErTable
{
    public string TableName { get; set; } = string.Empty;
    public string SchemaName { get; set; } = string.Empty;
    public List<ErColumn> Columns { get; set; } = new();
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 200;
    public double Height { get; set; }
    public string? ColorHex { get; set; }
    public string? ColorGroup { get; set; }
}

public class ErColumn
{
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public bool IsPrimaryKey { get; set; }
    public bool IsForeignKey { get; set; }
    public bool IsNullable { get; set; } = true;
}

public class ErRelationship
{
    public string FromTable { get; set; } = string.Empty;
    public string FromColumn { get; set; } = string.Empty;
    public string ToTable { get; set; } = string.Empty;
    public string ToColumn { get; set; } = string.Empty;
}

public class ErDiagramModel
{
    public List<ErTable> Tables { get; set; } = new();
    public List<ErRelationship> Relationships { get; set; } = new();
}
