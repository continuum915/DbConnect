using System;
using System.Collections.Generic;

namespace DbConnect.Models;

/// <summary>
/// A structured, redacted diagnostic event suitable for local logs and support bundles.
/// The model intentionally contains a connection identity rather than a connection string.
/// </summary>
public sealed class DiagnosticEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public DiagnosticSeverity Severity { get; init; }
    public string Subsystem { get; init; } = string.Empty;
    public string Operation { get; init; } = string.Empty;
    public string CorrelationId { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public string ConnectionIdentity { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? ExceptionType { get; init; }
    public string? StackTrace { get; init; }
    public IReadOnlyDictionary<string, string?> Properties { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
}
