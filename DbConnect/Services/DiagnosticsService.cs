using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Local structured diagnostics sink. Writes are best-effort and never allowed
/// to replace the original application failure. Every persisted field is redacted.
/// </summary>
public sealed class DiagnosticsService
{
    private static readonly Lazy<DiagnosticsService> DefaultInstance =
        new(() => new DiagnosticsService());
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly object _gate = new();
    private readonly int _maxLogBytes;
    private readonly int _maxEntries;

    public DiagnosticsService(
        string? rootDirectory = null,
        int maxLogBytes = 5 * 1024 * 1024,
        int maxEntries = 10_000)
    {
        RootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? Path.Combine(AppDataPaths.RootDirectory, "diagnostics")
            : rootDirectory;
        _maxLogBytes = Math.Max(1, maxLogBytes);
        _maxEntries = Math.Max(1, maxEntries);
    }

    public static DiagnosticsService Default => DefaultInstance.Value;
    public string RootDirectory { get; }
    public string LogPath => Path.Combine(RootDirectory, "events.jsonl");

    public DiagnosticOperationContext BeginOperation(
        string subsystem,
        string operation,
        string? provider = null,
        string? connectionIdentity = null,
        string? correlationId = null) =>
        DiagnosticOperationContext.Create(
            DiagnosticRedactor.Redact(subsystem),
            DiagnosticRedactor.Redact(operation),
            DiagnosticRedactor.Redact(provider),
            DiagnosticRedactor.SanitizeConnectionIdentity(connectionIdentity),
            correlationId);

    public DiagnosticEvent Record(
        DiagnosticOperationContext context,
        DiagnosticSeverity severity,
        string message,
        IReadOnlyDictionary<string, string?>? properties = null,
        Exception? exception = null) =>
        Record(new DiagnosticEvent
        {
            Severity = severity,
            Subsystem = context.Subsystem,
            Operation = context.Operation,
            CorrelationId = context.CorrelationId,
            Provider = context.Provider,
            ConnectionIdentity = context.ConnectionIdentity,
            Message = message,
            ExceptionType = exception?.GetType().FullName,
            StackTrace = exception?.ToString(),
            Properties = properties ?? new Dictionary<string, string?>()
        });

    public DiagnosticEvent RecordFailure(
        DiagnosticOperationContext context,
        Exception exception,
        string? message = null,
        IReadOnlyDictionary<string, string?>? properties = null) =>
        Record(
            context,
            DiagnosticSeverity.Error,
            message ?? exception.Message,
            properties,
            exception);

    public DiagnosticEvent Record(DiagnosticEvent diagnosticEvent)
    {
        var safe = Sanitize(diagnosticEvent);
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(RootDirectory);
                File.AppendAllText(LogPath, JsonSerializer.Serialize(safe, JsonOptions) + Environment.NewLine);
                TrimIfNeeded();
            }
        }
        catch
        {
            // Diagnostics must never mask or change the operation being diagnosed.
        }

        return safe;
    }

    public async Task<IReadOnlyList<DiagnosticEvent>> ReadRecentAsync(int limit = 1000)
    {
        if (limit <= 0 || !File.Exists(LogPath))
            return Array.Empty<DiagnosticEvent>();

        try
        {
            var lines = await File.ReadAllLinesAsync(LogPath);
            var result = new List<DiagnosticEvent>(Math.Min(limit, lines.Length));
            foreach (var line in lines.Reverse().Take(limit))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<DiagnosticEvent>(line, JsonOptions);
                    if (entry != null)
                        result.Add(entry);
                }
                catch
                {
                    // Skip a torn or manually edited line and retain the rest of the bundle.
                }
            }

            result.Reverse();
            return result;
        }
        catch
        {
            return Array.Empty<DiagnosticEvent>();
        }
    }

    public Task ClearAsync()
    {
        try
        {
            lock (_gate)
            {
                if (File.Exists(LogPath))
                    File.Delete(LogPath);
            }
        }
        catch
        {
            // Best effort; clearing diagnostics is never an application failure.
        }

        return Task.CompletedTask;
    }

    private static DiagnosticEvent Sanitize(DiagnosticEvent source)
    {
        var properties = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source.Properties)
        {
            properties[DiagnosticRedactor.Redact(pair.Key)] =
                DiagnosticRedactor.IsSensitiveKey(pair.Key)
                    ? "[REDACTED]"
                    : DiagnosticRedactor.Redact(pair.Value);
        }

        return new DiagnosticEvent
        {
            Timestamp = source.Timestamp == default ? DateTimeOffset.UtcNow : source.Timestamp,
            Severity = source.Severity,
            Subsystem = DiagnosticRedactor.Redact(source.Subsystem),
            Operation = DiagnosticRedactor.Redact(source.Operation),
            CorrelationId = DiagnosticRedactor.Redact(source.CorrelationId),
            Provider = DiagnosticRedactor.Redact(source.Provider),
            ConnectionIdentity = DiagnosticRedactor.SanitizeConnectionIdentity(source.ConnectionIdentity),
            Message = DiagnosticRedactor.Redact(source.Message),
            ExceptionType = DiagnosticRedactor.Redact(source.ExceptionType),
            StackTrace = DiagnosticRedactor.Redact(source.StackTrace),
            Properties = properties
        };
    }

    private void TrimIfNeeded()
    {
        var info = new FileInfo(LogPath);
        if (!info.Exists || (info.Length <= _maxLogBytes && CountLines(LogPath) <= _maxEntries))
            return;

        var lines = File.ReadAllLines(LogPath);
        var kept = lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .TakeLast(_maxEntries)
            .ToList();

        while (kept.Count > 1 && EncodingSize(kept) > _maxLogBytes)
            kept.RemoveAt(0);

        File.WriteAllLines(LogPath, kept);
    }

    private static int CountLines(string path)
    {
        var count = 0;
        using var reader = new StreamReader(path);
        while (reader.ReadLine() != null)
            count++;
        return count;
    }

    private static long EncodingSize(IEnumerable<string> lines) =>
        lines.Sum(line => (long)System.Text.Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length);
}
