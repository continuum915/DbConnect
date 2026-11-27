using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Parses EXPLAIN / execution plan output from various providers into a structured tree.
/// </summary>
public class ExplainParserService
{
    public ExplainPlanNode? Parse(QueryResult raw, string providerName)
    {
        try
        {
            var node = providerName switch
            {
                "PostgreSQL" => ParsePostgreSQL(raw),
                "SQL Server" => ParseSQLServer(raw),
                "MySQL" => ParseMySQL(raw),
                _ => ParseGeneric(raw)
            };
            if (node != null)
                NormalizeRelativeCost(node);
            return node;
        }
        catch
        {
            return ParseGeneric(raw);
        }
    }

    // ── PostgreSQL ────────────────────────────────────────────────────────────

    public ExplainPlanNode? ParsePostgreSQL(QueryResult raw)
    {
        var lines = ExtractTextLines(raw);
        if (lines.Count == 0) return ParseGeneric(raw);

        var stack = new Stack<(ExplainPlanNode Node, int Depth)>();
        ExplainPlanNode? root = null;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            int leading = line.Length - line.TrimStart().Length;
            // Arrow lines "  ->  X" have deeper visual indent but mark a child
            var trimmed = line.TrimStart();
            bool isArrow = trimmed.StartsWith("->", StringComparison.Ordinal);
            if (isArrow)
                trimmed = trimmed[2..].TrimStart();

            int depth = leading / 2;

            var node = ParsePostgreSQLLine(trimmed, line);

            while (stack.Count > 0 && stack.Peek().Depth >= depth)
                stack.Pop();

            if (stack.Count == 0)
            {
                root = node;
            }
            else
            {
                stack.Peek().Node.Children.Add(node);
            }

            stack.Push((node, depth));
        }

        return root;
    }

    private static ExplainPlanNode ParsePostgreSQLLine(string trimmed, string rawLine)
    {
        var node = new ExplainPlanNode { RawText = rawLine };

        // cost=X..Y rows=Z
        var costMatch = Regex.Match(trimmed,
            @"\(cost=(\d+(?:\.\d+)?)\.\.(\d+(?:\.\d+)?)\s+rows=(\d+)",
            RegexOptions.IgnoreCase);
        if (costMatch.Success)
        {
            node.StartupCost = double.Parse(costMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            node.TotalCost = double.Parse(costMatch.Groups[2].Value, CultureInfo.InvariantCulture);
            node.EstimatedRows = double.Parse(costMatch.Groups[3].Value, CultureInfo.InvariantCulture);
        }

        // actual rows=A loops=B
        var actualMatch = Regex.Match(trimmed,
            @"actual\s+(?:time=[\d.]+\.\.[\d.]+\s+)?rows=(\d+)\s+loops=(\d+)",
            RegexOptions.IgnoreCase);
        if (actualMatch.Success)
        {
            node.ActualRows = double.Parse(actualMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            node.Loops = int.Parse(actualMatch.Groups[2].Value, CultureInfo.InvariantCulture);
        }

        // NodeType [on RelationName]
        var parenIdx = trimmed.IndexOf('(');
        var header = parenIdx >= 0 ? trimmed[..parenIdx].Trim() : trimmed.Trim();

        var onMatch = Regex.Match(header, @"^(.+?)\s+on\s+(\w+)\s*$", RegexOptions.IgnoreCase);
        if (onMatch.Success)
        {
            node.NodeType = onMatch.Groups[1].Value.Trim();
            node.RelationName = onMatch.Groups[2].Value.Trim();
        }
        else
        {
            node.NodeType = header;
        }

        return node;
    }

    // ── SQL Server ────────────────────────────────────────────────────────────

    public ExplainPlanNode? ParseSQLServer(QueryResult raw)
    {
        try
        {
            // The result may be XML in the first column
            var xmlText = raw.ObjectRows
                .Select(r => r.Length > 0 ? r[0]?.ToString() ?? string.Empty : string.Empty)
                .FirstOrDefault(v => v.TrimStart().StartsWith("<", StringComparison.Ordinal));

            if (string.IsNullOrEmpty(xmlText))
                return ParseGeneric(raw);

            var doc = XDocument.Parse(xmlText);
            XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;

            var root = new ExplainPlanNode { NodeType = "ShowPlan", RawText = xmlText };
            ParseSQLServerElement(doc.Root, root, ns);
            return root.Children.Count > 0 ? root : ParseGeneric(raw);
        }
        catch
        {
            return ParseGeneric(raw);
        }
    }

    private static void ParseSQLServerElement(XElement? element, ExplainPlanNode parent, XNamespace ns)
    {
        if (element == null) return;
        foreach (var relOp in element.Descendants(ns + "RelOp"))
        {
            var node = new ExplainPlanNode
            {
                NodeType = relOp.Attribute("PhysicalOp")?.Value ?? relOp.Attribute("LogicalOp")?.Value ?? "RelOp",
                EstimatedRows = double.TryParse(relOp.Attribute("EstimateRows")?.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var er) ? er : 0,
                TotalCost = double.TryParse(relOp.Attribute("EstimatedTotalSubtreeCost")?.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var tc) ? tc : 0,
                RawText = relOp.ToString()
            };
            parent.Children.Add(node);
            // Only top-level RelOps are added; children are handled by nesting
            break;
        }
    }

    // ── MySQL ─────────────────────────────────────────────────────────────────

    public ExplainPlanNode? ParseMySQL(QueryResult raw)
    {
        if (!raw.HasRows) return ParseGeneric(raw);

        var root = new ExplainPlanNode { NodeType = "EXPLAIN", RawText = string.Empty };
        foreach (var row in raw.ObjectRows)
        {
            var node = new ExplainPlanNode
            {
                NodeType = raw.ValueOrDefault(row, "select_type")?.ToString() ?? "SELECT",
                RelationName = raw.ValueOrDefault(row, "table")?.ToString() ?? string.Empty,
                RawText = string.Join(", ", raw.ColumnInfos.Select((c, i) => $"{c.Name}={(i < row.Length ? row[i] : null)}"))
            };
            if (double.TryParse(raw.ValueOrDefault(row, "rows")?.ToString(), out var rows))
                node.EstimatedRows = rows;
            root.Children.Add(node);
        }

        return root;
    }

    // ── Generic fallback ──────────────────────────────────────────────────────

    public ExplainPlanNode ParseGeneric(QueryResult raw)
    {
        var lines = ExtractTextLines(raw);
        return new ExplainPlanNode
        {
            NodeType = "Plan",
            RawText = lines.Count > 0 ? string.Join(Environment.NewLine, lines) : "(no plan data)"
        };
    }

    // ── Normalization ─────────────────────────────────────────────────────────

    private static void NormalizeRelativeCost(ExplainPlanNode root)
    {
        var allNodes = new List<ExplainPlanNode>();
        CollectAll(root, allNodes);

        double max = allNodes.Max(n => n.TotalCost);
        if (max <= 0) return;

        foreach (var n in allNodes)
            n.RelativeCost = n.TotalCost / max;
    }

    private static void CollectAll(ExplainPlanNode node, List<ExplainPlanNode> result)
    {
        result.Add(node);
        foreach (var child in node.Children)
            CollectAll(child, result);
    }

    // ── Utility ───────────────────────────────────────────────────────────────

    private static List<string> ExtractTextLines(QueryResult raw)
    {
        if (!raw.HasRows) return new List<string>();

        return raw.ObjectRows
            .Select(r => r.Length > 0 ? r[0]?.ToString() ?? string.Empty : string.Empty)
            .Where(l => l != null)
            .ToList();
    }
}
