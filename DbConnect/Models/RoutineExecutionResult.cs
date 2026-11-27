using System.Collections.Generic;

namespace DbConnect.Models;

public class RoutineExecutionResult
{
    public List<QueryResult> ResultSets { get; init; } = new();
    public List<string> Messages { get; init; } = new();
    public List<(string Name, object? Value)> OutputParameters { get; init; } = new();
}
