namespace DbConnect.Models;

public record DbParameterValue(
    string Name,
    object? Value,
    string DataType,
    ParameterDirection Direction);
