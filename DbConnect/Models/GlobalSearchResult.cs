namespace DbConnect.Models;

public class GlobalSearchResult
{
    public string ObjectName { get; set; } = string.Empty;
    public string ObjectType { get; set; } = string.Empty;
    public string SchemaName { get; set; } = string.Empty;
    public string ConnectionName { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public SchemaNode? Node { get; set; }
}
