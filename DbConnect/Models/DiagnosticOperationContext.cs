using System;

namespace DbConnect.Models;

/// <summary>
/// Correlation metadata carried by a long-running operation and each event it emits.
/// </summary>
public sealed record DiagnosticOperationContext(
    string CorrelationId,
    string Subsystem,
    string Operation,
    string Provider,
    string ConnectionIdentity)
{
    public static DiagnosticOperationContext Create(
        string subsystem,
        string operation,
        string? provider = null,
        string? connectionIdentity = null,
        string? correlationId = null) =>
        new(
            string.IsNullOrWhiteSpace(correlationId) ? Guid.NewGuid().ToString("N") : correlationId,
            subsystem,
            operation,
            provider ?? string.Empty,
            connectionIdentity ?? string.Empty);
}
