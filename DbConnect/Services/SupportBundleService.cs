using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Builds an inspectable, redacted support bundle. Draft generation is separate
/// from saving so users can review the exact files before an archive is written.
/// </summary>
public sealed class SupportBundleService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly DiagnosticsService _diagnostics;
    private readonly DatabaseService _databaseService;

    public SupportBundleService(
        DiagnosticsService? diagnostics = null,
        DatabaseService? databaseService = null)
    {
        _diagnostics = diagnostics ?? DiagnosticsService.Default;
        _databaseService = databaseService ?? new DatabaseService();
    }

    public async Task<SupportBundleDraft> BuildDraftAsync(SupportBundleOptions? options = null)
    {
        options ??= new SupportBundleOptions();
        var createdAt = DateTimeOffset.UtcNow;
        var warnings = new List<string>();
        var entries = new List<SupportBundleEntry>();

        IReadOnlyList<DiagnosticEvent> events;
        try
        {
            events = await _diagnostics.ReadRecentAsync(Math.Clamp(options.MaxDiagnosticEvents, 1, 10_000));
        }
        catch (Exception ex)
        {
            events = Array.Empty<DiagnosticEvent>();
            warnings.Add($"Diagnostics could not be read: {DiagnosticRedactor.Redact(ex.Message)}");
        }

        entries.Add(new SupportBundleEntry(
            "diagnostics/events.jsonl",
            "application/x-ndjson",
            SerializeLines(events)));
        entries.Add(new SupportBundleEntry(
            "diagnostics/failures.jsonl",
            "application/x-ndjson",
            SerializeLines(events.Where(e => e.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Critical))));

        try
        {
            entries.Add(new SupportBundleEntry(
                "provider-capabilities.json",
                "application/json",
                JsonSerializer.Serialize(BuildProviderSnapshot(options.Sessions), JsonOptions)));
        }
        catch (Exception ex)
        {
            warnings.Add($"Provider capability metadata could not be collected: {DiagnosticRedactor.Redact(ex.Message)}");
        }

        try
        {
            var settings = options.SettingsSummary.ToDictionary(
                pair => DiagnosticRedactor.Redact(pair.Key),
                pair => DiagnosticRedactor.IsSensitiveKey(pair.Key)
                    ? "[REDACTED]"
                    : DiagnosticRedactor.Redact(pair.Value),
                StringComparer.OrdinalIgnoreCase);
            entries.Add(new SupportBundleEntry(
                "settings-summary.json",
                "application/json",
                JsonSerializer.Serialize(settings, JsonOptions)));
        }
        catch (Exception ex)
        {
            warnings.Add($"Settings summary could not be collected: {DiagnosticRedactor.Redact(ex.Message)}");
        }

        entries.Add(new SupportBundleEntry(
            "README.txt",
            "text/plain",
            "DbConnect support bundle\r\n\r\n" +
            "This bundle contains redacted diagnostics and runtime metadata. " +
            "Saved query text, schema definitions, table data, connection strings, and credentials are excluded by default.\r\n" +
            "Review the draft contents before saving or sharing it.\r\n"));

        var manifest = new
        {
            schemaVersion = 1,
            createdAt,
            applicationVersion = DiagnosticRedactor.Redact(options.ApplicationVersion ?? GetApplicationVersion()),
            operatingSystem = DiagnosticRedactor.Redact(RuntimeInformation.OSDescription),
            framework = DiagnosticRedactor.Redact(RuntimeInformation.FrameworkDescription),
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            entries = entries.Select(entry => new { entry.Name, entry.ContentType }),
            warnings
        };
        entries.Insert(0, new SupportBundleEntry(
            "manifest.json",
            "application/json",
            JsonSerializer.Serialize(manifest, JsonOptions)));

        return new SupportBundleDraft(createdAt, entries);
    }

    public async Task SaveAsync(SupportBundleDraft draft, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        foreach (var entry in draft.Entries)
        {
            var archiveEntry = archive.CreateEntry(entry.Name, CompressionLevel.Optimal);
            await using var entryStream = archiveEntry.Open();
            await using var writer = new StreamWriter(entryStream);
            await writer.WriteAsync(entry.Content);
        }
    }

    private IEnumerable<object> BuildProviderSnapshot(IReadOnlyList<DatabaseSessionSummary> sessions)
    {
        var sessionByProvider = sessions
            .GroupBy(session => session.Provider, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var providerName in _databaseService.GetAvailableProviders().OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            var provider = _databaseService.GetProvider(providerName);
            sessionByProvider.TryGetValue(providerName, out var session);
            yield return new
            {
                provider = provider.ProviderName,
                transactionSupport = provider.Capabilities.TransactionSupport.ToString(),
                supportsTransactionalBatches = provider.Capabilities.SupportsTransactionalBatches,
                guaranteesTransactionalBatches = provider.Capabilities.GuaranteesTransactionalBatches,
                supportsSchemas = provider.Capabilities.SupportsSchemas,
                supportsViews = provider.Capabilities.SupportsViews,
                supportsIndexes = provider.Capabilities.SupportsIndexes,
                supportsTriggers = provider.Capabilities.SupportsTriggers,
                supportsFunctions = provider.Capabilities.SupportsFunctions,
                supportsProcedures = provider.Capabilities.SupportsProcedures,
                supportsUserWrite = provider.Capabilities.SupportsUserWrite,
                session = session is null
                    ? null
                    : new
                    {
                        name = DiagnosticRedactor.Redact(session.Name),
                        environmentTier = DiagnosticRedactor.Redact(session.EnvironmentTier),
                        database = DiagnosticRedactor.Redact(session.Database)
                    }
            };
        }
    }

    private static string SerializeLines(IEnumerable<DiagnosticEvent> events) =>
        string.Join(
            Environment.NewLine,
            events.Select(entry => JsonSerializer.Serialize(entry, JsonOptions))) +
        (events.Any() ? Environment.NewLine : string.Empty);

    private static string GetApplicationVersion() =>
        typeof(SupportBundleService).Assembly.GetName().Version?.ToString(3) ?? "unknown";
}
