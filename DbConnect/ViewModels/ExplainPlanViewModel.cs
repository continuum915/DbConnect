using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class ExplainPlanViewModel : ViewModelBase
{
    private readonly ExplainParserService _parser = new();

    public ExplainPlanViewModel()
    {
        Title = "Explain Plan";
    }

    // ── Properties ────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStructuredPlan))]
    [NotifyPropertyChangedFor(nameof(RootNodes))]
    private ExplainPlanNode? _rootNode;

    [ObservableProperty]
    private string _rawText = string.Empty;

    [ObservableProperty]
    private string _providerName = string.Empty;

    public bool HasStructuredPlan =>
        RootNode != null && RootNode.Children.Count > 0;

    /// <summary>Wraps RootNode in a list for TreeView binding.</summary>
    public IReadOnlyList<ExplainPlanNode> RootNodes =>
        RootNode != null
            ? new[] { RootNode }
            : Array.Empty<ExplainPlanNode>();

    // ── Load ──────────────────────────────────────────────────────────────────

    public void LoadFromResult(QueryResult result, string providerName)
    {
        ProviderName = providerName;

        // Collect raw text lines for fallback display
        RawText = result.HasRows
            ? string.Join(Environment.NewLine,
                result.ObjectRows.Select(r =>
                    r.Length > 0
                        ? r[0]?.ToString() ?? string.Empty
                        : string.Empty))
            : result.ErrorMessage ?? string.Empty;

        // Build structured plan
        RootNode = _parser.Parse(result, providerName);
    }
}
