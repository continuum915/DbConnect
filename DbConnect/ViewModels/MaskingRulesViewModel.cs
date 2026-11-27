using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class MaskingRulesViewModel : ViewModelBase
{
    private readonly DataMaskingService _service = new();

    public override string Title => "Masking Rules";

    [ObservableProperty] private ObservableCollection<MaskingRule> _rules = new();
    [ObservableProperty] private ObservableCollection<string> _availableColumns = new();
    [ObservableProperty] private bool _isVisible;

    [RelayCommand]
    private void AddRule()
    {
        Rules.Add(new MaskingRule
        {
            ColumnName = AvailableColumns.Count > 0 ? AvailableColumns[0] : string.Empty,
            Method = MaskingMethod.NullOut
        });
    }

    [RelayCommand]
    private void RemoveRule(MaskingRule? rule)
    {
        if (rule != null) Rules.Remove(rule);
    }

    [RelayCommand]
    private void LoadTemplate(string templateName)
    {
        var templates = _service.GetBuiltInTemplates();
        foreach (var t in templates)
        {
            if (t.ColumnName.Equals(templateName, System.StringComparison.OrdinalIgnoreCase))
            {
                Rules.Add(new MaskingRule
                {
                    ColumnName = t.ColumnName,
                    Method = t.Method,
                    ConstantValue = t.ConstantValue,
                    RegexPattern = t.RegexPattern,
                    RegexReplacement = t.RegexReplacement,
                    FormatTemplate = t.FormatTemplate
                });
                return;
            }
        }
        // If not found by name, add first template
        if (templates.Count > 0)
        {
            var t = templates[0];
            Rules.Add(new MaskingRule
            {
                ColumnName = t.ColumnName,
                Method = t.Method,
                ConstantValue = t.ConstantValue,
                RegexPattern = t.RegexPattern,
                RegexReplacement = t.RegexReplacement,
                FormatTemplate = t.FormatTemplate
            });
        }
    }

    public System.Collections.Generic.IReadOnlyList<string> BuiltInTemplateNames =>
    [
        "email", "phone", "credit_card", "password"
    ];

    public string? ApplyMaskForColumn(string columnName, string? value,
        System.Collections.Generic.Dictionary<string, string?> row)
    {
        foreach (var rule in Rules)
        {
            if (rule.ColumnName.Equals(columnName, System.StringComparison.OrdinalIgnoreCase))
                return _service.ApplyMask(rule, value, row);
        }
        return value;
    }
}
