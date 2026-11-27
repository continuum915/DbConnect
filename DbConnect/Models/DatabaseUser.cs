using System.Collections.Generic;

namespace DbConnect.Models;

public class DatabaseUser
{
    public string Name { get; set; } = string.Empty;
    public bool IsRole { get; set; }
    public List<string> MemberOf { get; set; } = new();
    public List<string> Privileges { get; set; } = new();
}

public class PermissionGrant
{
    public string Grantee { get; set; } = string.Empty;
    public string ObjectType { get; set; } = string.Empty;
    public string ObjectName { get; set; } = string.Empty;
    public string Privilege { get; set; } = string.Empty;
    public bool WithGrantOption { get; set; }
}
