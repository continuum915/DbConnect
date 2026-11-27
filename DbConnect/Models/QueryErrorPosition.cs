namespace DbConnect.Models;

public readonly record struct QueryErrorPosition(int? Offset = null, int? Line = null);
