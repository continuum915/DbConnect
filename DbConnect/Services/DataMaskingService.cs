using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DbConnect.Models;

namespace DbConnect.Services;

public class DataMaskingService
{
    public string? ApplyMask(MaskingRule rule, string? originalValue, Dictionary<string, string?> rowValues)
    {
        return rule.Method switch
        {
            MaskingMethod.NullOut => null,
            MaskingMethod.ConstantValue => rule.ConstantValue,
            MaskingMethod.HashSha256 => HashSha256(originalValue),
            MaskingMethod.RegexReplace => ApplyRegex(rule, originalValue),
            MaskingMethod.FormatTemplate => ApplyTemplate(rule.FormatTemplate, rowValues),
            _ => originalValue
        };
    }

    private static string? HashSha256(string? value)
    {
        if (value == null) return null;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string? ApplyRegex(MaskingRule rule, string? value)
    {
        if (value == null || rule.RegexPattern == null) return value;
        return Regex.Replace(value, rule.RegexPattern, rule.RegexReplacement ?? string.Empty);
    }

    private static string? ApplyTemplate(string? template, Dictionary<string, string?> rowValues)
    {
        if (template == null) return null;
        var result = template;
        foreach (var (col, val) in rowValues)
            result = result.Replace($"{{{col}}}", val ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        return result;
    }

    public IReadOnlyList<MaskingRule> GetBuiltInTemplates() =>
    [
        new MaskingRule
        {
            ColumnName = "email",
            Method = MaskingMethod.RegexReplace,
            RegexPattern = @"^[^@]+",
            RegexReplacement = "masked"
        },
        new MaskingRule
        {
            ColumnName = "phone",
            Method = MaskingMethod.RegexReplace,
            RegexPattern = @"\d",
            RegexReplacement = "X"
        },
        new MaskingRule
        {
            ColumnName = "credit_card",
            Method = MaskingMethod.RegexReplace,
            RegexPattern = @"\d(?=\d{4})",
            RegexReplacement = "*"
        },
        new MaskingRule
        {
            ColumnName = "password",
            Method = MaskingMethod.ConstantValue,
            ConstantValue = "***MASKED***"
        }
    ];
}
