using Avalonia;

namespace DbConnect.Models;

public enum ErRelationshipCardinality
{
    One,
    Many,
    ZeroOrOne,
    ZeroOrMany,
    OneAndOnlyOne,
    OneOrMany
}

public class ErRelationshipVisual
{
    public string Name { get; set; } = string.Empty;
    public string FromTable { get; set; } = string.Empty;
    public string FromColumn { get; set; } = string.Empty;
    public string ToTable { get; set; } = string.Empty;
    public string ToColumn { get; set; } = string.Empty;
    public double StartX { get; set; }
    public double StartY { get; set; }
    public double EndX { get; set; }
    public double EndY { get; set; }
    public ErRelationshipCardinality StartCardinality { get; set; } = ErRelationshipCardinality.Many;
    public ErRelationshipCardinality EndCardinality { get; set; } = ErRelationshipCardinality.One;
    public Point StartPoint => new(StartX, StartY);
    public Point EndPoint => new(EndX, EndY);
}
