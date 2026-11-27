using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using DbConnect.Models;

namespace DbConnect.Services;

public class BackupService
{
    private readonly DiagnosticsService _diagnostics;

    public BackupService(DiagnosticsService? diagnostics = null)
    {
        _diagnostics = diagnostics ?? DiagnosticsService.Default;
    }

    private static string ProfilesPath =>
        Path.Combine(AppDataPaths.RootDirectory, "backup-profiles.json");

    // ── SQLite ────────────────────────────────────────────────────────────────

    public Task BackupSqliteAsync(string connectionString, string outputPath, IProgress<string> progress) =>
        ExecuteWithDiagnosticsAsync("backup", "SQLite", "SQLite", async () =>
        {
            progress.Report("Starting SQLite backup...");
            await using var sourceConn = new SqliteConnection(connectionString);
            await sourceConn.OpenAsync();
            await using var destConn = new SqliteConnection($"Data Source={outputPath}");
            await destConn.OpenAsync();
            sourceConn.BackupDatabase(destConn);
            progress.Report("Backup complete.");
        });

    // ── PostgreSQL ────────────────────────────────────────────────────────────

    public Task BackupPostgreSqlAsync(string connectionString, string outputPath, IProgress<string> progress) =>
        ExecuteWithDiagnosticsAsync("backup", "PostgreSQL", "PostgreSQL", async () =>
        {
            progress.Report("Starting PostgreSQL backup via pg_dump...");
            var psi = PostgreSqlBackupStartInfo.CreateBackup(connectionString, outputPath);

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start pg_dump.");

            string stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"pg_dump failed (exit {proc.ExitCode}): {stderr}");

            progress.Report("Backup complete.");
        });

    public Task RestorePostgreSqlAsync(string connectionString, string inputPath, IProgress<string> progress) =>
        ExecuteWithDiagnosticsAsync("restore", "PostgreSQL", "PostgreSQL", async () =>
        {
            progress.Report("Starting PostgreSQL restore via pg_restore...");
            var psi = PostgreSqlBackupStartInfo.CreateRestore(connectionString, inputPath);

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start pg_restore.");

            string stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"pg_restore failed (exit {proc.ExitCode}): {stderr}");

            progress.Report("Restore complete.");
        });

    // ── SQL Server ────────────────────────────────────────────────────────────

    public Task BackupSqlServerAsync(DatabaseSession session, string outputPath, IProgress<string> progress) =>
        ExecuteWithDiagnosticsAsync("backup", "SQL Server", session.Name, async () =>
        {
            progress.Report("Starting SQL Server backup...");
            // Extract database name from connection string (best-effort)
            var dbName = ExtractSqlServerDatabase(session.ConnectionString);
            var sql = $"BACKUP DATABASE [{dbName}] TO DISK = N'{outputPath.Replace("'", "''")}' WITH FORMAT, INIT, STATS = 10";
            await session.ExecuteNonQueryAsync(sql);
            progress.Report("Backup complete.");
        });

    public Task RestoreSqlServerAsync(DatabaseSession session, string inputPath, IProgress<string> progress) =>
        ExecuteWithDiagnosticsAsync("restore", "SQL Server", session.Name, async () =>
        {
            progress.Report("Starting SQL Server restore...");
            var dbName = ExtractSqlServerDatabase(session.ConnectionString);
            var sql = $"RESTORE DATABASE [{dbName}] FROM DISK = N'{inputPath.Replace("'", "''")}' WITH REPLACE, STATS = 10";
            await session.ExecuteNonQueryAsync(sql);
            progress.Report("Restore complete.");
        });

    // ── DbConnect's dump (MySQL) ───────────────────────────────────────────────────

    public Task BackupWithDumpAsync(DatabaseSession session, string outputPath, IProgress<string> progress, CancellationToken cancellationToken) =>
        ExecuteWithDiagnosticsAsync("backup", session.ProviderName, session.Name,
            () => DumpBackup.RunAsync(session, outputPath, progress, cancellationToken));

    public Task RestoreWithScriptAsync(DatabaseSession session, string inputPath, IProgress<string> progress, CancellationToken cancellationToken) =>
        ExecuteWithDiagnosticsAsync("restore", session.ProviderName, session.Name,
            () => DumpRestore.RunAsync(session, inputPath, progress, cancellationToken));

    // ── pg_dump availability ──────────────────────────────────────────────────

    public async Task<bool> CheckPgDumpAvailableAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pg_dump",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task ExecuteWithDiagnosticsAsync(
        string operationName,
        string provider,
        string connectionIdentity,
        Func<Task> operation)
    {
        var context = _diagnostics.BeginOperation("backup", operationName, provider, connectionIdentity);
        try
        {
            await operation();
            _diagnostics.Record(context, DiagnosticSeverity.Information, "Backup operation completed.");
        }
        catch (Exception ex)
        {
            _diagnostics.RecordFailure(context, ex, "Backup operation failed.");
            throw;
        }
    }

    // ── Profile CRUD ──────────────────────────────────────────────────────────

    public async Task<List<BackupProfile>> LoadProfilesAsync()
    {
        try
        {
            if (!File.Exists(ProfilesPath))
                return new List<BackupProfile>();
            var json = await File.ReadAllTextAsync(ProfilesPath);
            return JsonSerializer.Deserialize<List<BackupProfile>>(json) ?? new List<BackupProfile>();
        }
        catch
        {
            return new List<BackupProfile>();
        }
    }

    public async Task SaveProfileAsync(BackupProfile profile)
    {
        var profiles = await LoadProfilesAsync();
        var existing = profiles.FindIndex(p => p.Id == profile.Id);
        if (existing >= 0)
            profiles[existing] = profile;
        else
            profiles.Add(profile);
        await WriteProfilesAsync(profiles);
    }

    public async Task DeleteProfileAsync(string id)
    {
        var profiles = await LoadProfilesAsync();
        profiles.RemoveAll(p => p.Id == id);
        await WriteProfilesAsync(profiles);
    }

    private static async Task WriteProfilesAsync(List<BackupProfile> profiles)
    {
        var dir = Path.GetDirectoryName(ProfilesPath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(ProfilesPath, json);
    }

    // ── Connection string helpers ─────────────────────────────────────────────

    private static string ExtractSqlServerDatabase(string connectionString)
    {
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf('=');
            if (idx < 0) continue;
            var key = part[..idx].Trim().ToLowerInvariant();
            var val = part[(idx + 1)..].Trim();
            if (key is "database" or "initial catalog")
                return val;
        }
        return "Unknown";
    }
}
