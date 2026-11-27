using System;

namespace DbConnect.Models;

public class QueryParamEntry
{
    public string Placeholder { get; set; } = string.Empty;
    public object? Value { get; set; }
    public string DataType { get; set; } = "string"; // Default to string for literal substitution
}
