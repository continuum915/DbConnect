using System;

namespace DbConnect.Models;

public class BackupProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public bool CompressOutput { get; set; }
    public string AdditionalArgs { get; set; } = string.Empty;
}
