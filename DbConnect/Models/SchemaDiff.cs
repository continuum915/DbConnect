using System.Collections.Generic;
using System.ComponentModel;

namespace DbConnect.Models;

public enum DiffStatus { OnlyInSource, InBoth, OnlyInTarget }

public class SchemaDiffItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string ObjectName { get; set; } = string.Empty;
    public string? SchemaName { get; set; }
    public string ObjectType { get; set; } = string.Empty;
    public SchemaDiffObjectType ObjectKind
    {
        get => SchemaDiffObjectTypeExtensions.Parse(ObjectType);
        set => ObjectType = value.ToObjectTypeName();
    }

    public string QualifiedName => string.IsNullOrWhiteSpace(SchemaName)
        ? ObjectName
        : $"{SchemaName}.{ObjectName}";

    public DiffStatus Status { get; set; }
    public SchemaDiffChangeType ChangeType { get; set; }
    public string? SourceDdl { get; set; }
    public string? TargetDdl { get; set; }
    public string GeneratedScript { get; set; } = string.Empty;
    public List<string> DependsOn { get; set; } = new();

    private bool _isSelected = true;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}

public class SchemaDiffResult
{
    public List<SchemaDiffItem> Items { get; set; } = new();
    public string SourceConnectionName { get; set; } = string.Empty;
    public string TargetConnectionName { get; set; } = string.Empty;
}
