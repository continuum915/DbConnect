using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public sealed class R0DiagnosticsTests
{
    [Fact]
    public void Redactor_RemovesCredentialsTokensAndPrivateKeys()
    {
        var input = "Server=db;Database=rain;Password=super-secret;Token=abc123 " +
                    "Bearer bearer-secret https://user:uri-secret@example.test/db\n" +
                    "credentialStore=stored-secret " +
                    "-----BEGIN PRIVATE KEY-----\nprivate-secret\n-----END PRIVATE KEY-----";

        var redacted = DiagnosticRedactor.Redact(input);

        Assert.DoesNotContain("super-secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("bearer-secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("uri-secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("stored-secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("Server=db;Database=rain", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("private-secret", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED_PRIVATE_KEY]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redactor_RedactsSensitiveParameterValues()
    {
        var parameters = new Dictionary<string, object?>
        {
            ["customerId"] = 42,
            ["password"] = "secret-value",
            ["requestToken"] = "token-value"
        };

        var safe = DiagnosticRedactor.RedactParameters(parameters);

        Assert.Equal("42", safe["customerId"]);
        Assert.Equal("[REDACTED]", safe["password"]);
        Assert.Equal("[REDACTED]", safe["requestToken"]);
    }

    [Fact]
    public void Redactor_DoesNotPersistConnectionStringShapedIdentities()
    {
        var identity = DiagnosticRedactor.SanitizeConnectionIdentity(
            "Server=db.internal;Database=rain;Password=super-secret");

        Assert.Equal("[connection string redacted]", identity);
        Assert.DoesNotContain("db.internal", identity, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiagnosticsService_PersistsStructuredRedactedEventsWithCorrelation()
    {
        var root = TempDirectory();
        try
        {
            var service = new DiagnosticsService(root);
            var operation = service.BeginOperation("query", "execute", "SQLite", "Local dev");
            var recorded = service.RecordFailure(
                operation,
                new InvalidOperationException("Password=super-secret; query failed"),
                properties: new Dictionary<string, string?>
                {
                    ["password"] = "another-secret",
                    ["rows"] = "0"
                });

            var events = await service.ReadRecentAsync();

            var entry = Assert.Single(events);
            Assert.Equal(recorded.CorrelationId, entry.CorrelationId);
            Assert.Equal(DiagnosticSeverity.Error, entry.Severity);
            Assert.Equal("query", entry.Subsystem);
            Assert.Equal("SQLite", entry.Provider);
            Assert.DoesNotContain("super-secret", File.ReadAllText(service.LogPath), StringComparison.Ordinal);
            Assert.DoesNotContain("another-secret", File.ReadAllText(service.LogPath), StringComparison.Ordinal);
            Assert.Equal("[REDACTED]", entry.Properties["password"]);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task DatabaseSession_AttachesProviderFailuresToCorrelationContext()
    {
        var root = TempDirectory();
        try
        {
            var diagnostics = new DiagnosticsService(root);
            var session = new DatabaseSession(
                new ConnectionInfo
                {
                    Name = "Diagnostics query",
                    Provider = "SQLite",
                    ConnectionString = "Data Source=:memory:"
                },
                new SqliteProvider(),
                diagnostics);

            var result = await session.ExecuteQueryAsync("SELECT * FROM missing_table");
            var entry = Assert.Single(await diagnostics.ReadRecentAsync());

            Assert.True(result.HasError);
            Assert.Equal(DiagnosticSeverity.Error, entry.Severity);
            Assert.Equal("query", entry.Subsystem);
            Assert.False(string.IsNullOrWhiteSpace(entry.CorrelationId));
            Assert.Contains("missing_table", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task DiagnosticsService_RetainsBoundedNumberOfEvents()
    {
        var root = TempDirectory();
        try
        {
            var service = new DiagnosticsService(root, maxEntries: 100);
            var operation = service.BeginOperation("test", "retention");
            for (var i = 0; i < 125; i++)
                service.Record(operation, DiagnosticSeverity.Information, $"event-{i}");

            var events = await service.ReadRecentAsync(200);

            Assert.True(events.Count <= 100);
            Assert.Contains(events, entry => entry.Message == "event-124");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SupportBundle_DraftCanBeInspectedAndSavedWithoutSecrets()
    {
        var root = TempDirectory();
        var output = Path.Combine(root, "rain-support.zip");
        try
        {
            var diagnostics = new DiagnosticsService(Path.Combine(root, "diagnostics"));
            var operation = diagnostics.BeginOperation("backup", "create", "SQLite", "Production DB");
            diagnostics.RecordFailure(
                operation,
                new InvalidOperationException("Password=super-secret"),
                properties: new Dictionary<string, string?> { ["api_token"] = "token-secret" });

            var bundle = new SupportBundleService(diagnostics).BuildDraftAsync(new SupportBundleOptions
            {
                ApplicationVersion = "0.0.1-test",
                SettingsSummary = new Dictionary<string, string?>
                {
                    ["theme"] = "Dark",
                    ["password"] = "settings-secret"
                },
                Sessions =
                [
                    new DatabaseSessionSummary("Production DB", "SQLite", "Production", "rain")
                ]
            });
            var draft = await bundle;

            Assert.Contains("manifest.json", draft.EntryNames);
            Assert.Contains("diagnostics/failures.jsonl", draft.EntryNames);
            Assert.Contains("provider-capabilities.json", draft.EntryNames);
            Assert.Contains("README.txt", draft.EntryNames);
            Assert.Contains("diagnostics/events.jsonl", draft.InspectionText, StringComparison.Ordinal);
            Assert.Contains("\"provider\"", draft.Find("provider-capabilities.json")!.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("super-secret", string.Join("\n", draft.Entries.Select(entry => entry.Content)), StringComparison.Ordinal);
            Assert.DoesNotContain("token-secret", string.Join("\n", draft.Entries.Select(entry => entry.Content)), StringComparison.Ordinal);
            Assert.DoesNotContain("settings-secret", string.Join("\n", draft.Entries.Select(entry => entry.Content)), StringComparison.Ordinal);

            await new SupportBundleService(diagnostics).SaveAsync(draft, output);

            Assert.True(File.Exists(output));
            using var archive = ZipFile.OpenRead(output);
            Assert.Contains(archive.Entries, entry => entry.FullName == "manifest.json");
            Assert.Contains(archive.Entries, entry => entry.FullName == "settings-summary.json");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SupportBundle_SucceedsWhenDiagnosticsSourceIsUnavailable()
    {
        var root = TempDirectory();
        var diagnosticsPath = Path.Combine(root, "diagnostics-source");
        await File.WriteAllTextAsync(diagnosticsPath, "not a directory");
        try
        {
            var draft = await new SupportBundleService(new DiagnosticsService(diagnosticsPath)).BuildDraftAsync();

            Assert.NotEmpty(draft.Entries);
            Assert.Contains("manifest.json", draft.EntryNames);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain-r0-diagnostics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
