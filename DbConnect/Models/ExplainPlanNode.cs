using System.Collections.Generic;

namespace DbConnect.Models;

public class ExplainPlanNode
{
    public string NodeType { get; set; } = string.Empty;
    public string RelationName { get; set; } = string.Empty;
    public double EstimatedRows { get; set; }
    public double ActualRows { get; set; }
    public double TotalCost { get; set; }
    public double StartupCost { get; set; }
    public int Loops { get; set; } = 1;
    public string RawText { get; set; } = string.Empty;
    /// <summary>0.0–1.0 normalized across all nodes in the plan.</summary>
    public double RelativeCost { get; set; }
    public List<ExplainPlanNode> Children { get; set; } = new();

    /// <summary>Color indicator based on relative cost: green/yellow/red.</summary>
    public string CostIndicator => RelativeCost < 0.3 ? "🟢" : RelativeCost < 0.7 ? "🟡" : "🔴";

    /// <summary>Summary label shown in the tree node.</summary>
    public string Label =>
        string.IsNullOrEmpty(RelationName)
            ? $"{CostIndicator} {NodeType}"
            : $"{CostIndicator} {NodeType} on {RelationName}";

    /// <summary>Cost/row details shown as secondary info.</summary>
    public string Details =>
        ActualRows > 0
            ? $"  est:{EstimatedRows:N0}  act:{ActualRows:N0}  cost:{TotalCost:F2}"
            : $"  est:{EstimatedRows:N0}  cost:{TotalCost:F2}";
}
