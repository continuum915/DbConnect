namespace DbConnect.Models;

public class ObjectDependency
{
    public string ObjectName { get; set; } = string.Empty;
    public string ObjectType { get; set; } = string.Empty;
    public string DependsOnName { get; set; } = string.Empty;
    public string DependsOnType { get; set; } = string.Empty;
    public string DependencyKind { get; set; } = string.Empty;
}
