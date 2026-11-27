namespace DbConnect.Models;

public class ForeignKeyDefinition
{
    public string ConstraintName { get; set; } = "";
    public string Table { get; set; } = "";
    public string Column { get; set; } = "";
    public string ReferencedTable { get; set; } = "";
    public string ReferencedColumn { get; set; } = "";
    public string OnDelete { get; set; } = "NO ACTION";
    public string OnUpdate { get; set; } = "NO ACTION";
}
