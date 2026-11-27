namespace DbConnect.Models;

/// <summary>A cell set to its column default: UPDATE writes <c>SET col = DEFAULT</c>; INSERT leaves the column out.</summary>
public sealed class ColumnDefault
{
    public static ColumnDefault Value { get; } = new();

    private ColumnDefault() { }

    public override string ToString() => "DEFAULT";
}

public enum CellEditKind
{
    Null,
    EmptyString,
    Default,
    Text
}

/// <summary>What Set Value writes into the selected cells.</summary>
public sealed record CellEditValue(CellEditKind Kind, string? Text = null)
{
    public static CellEditValue Null { get; } = new(CellEditKind.Null);
    public static CellEditValue EmptyString { get; } = new(CellEditKind.EmptyString);
    public static CellEditValue Default { get; } = new(CellEditKind.Default);
}
