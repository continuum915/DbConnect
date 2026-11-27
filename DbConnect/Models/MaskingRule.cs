using System;

namespace DbConnect.Models;

public enum MaskingMethod { NullOut, ConstantValue, HashSha256, RegexReplace, FormatTemplate }

public class MaskingRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ColumnName { get; set; } = string.Empty;
    public MaskingMethod Method { get; set; }
    public string? ConstantValue { get; set; }
    public string? RegexPattern { get; set; }
    public string? RegexReplacement { get; set; }
    public string? FormatTemplate { get; set; }
}
