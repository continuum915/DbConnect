using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

public class SessionRecoveryService
{
    private readonly string _recoveryDir;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public SessionRecoveryService(string? recoveryDir = null)
    {
        _recoveryDir = recoveryDir ?? Path.Combine(AppDataPaths.RootDirectory, "recovery");
    }

    private void EnsureDirectory() => Directory.CreateDirectory(_recoveryDir);

    private string GetSnapshotPath(int pid, DateTimeOffset timestamp)
        => Path.Combine(_recoveryDir, $"session-{pid}-{timestamp:yyyyMMddHHmmss}.json");

    private string? _currentSnapshotPath;

    public async Task WriteSnapshotAsync(SessionSnapshot snapshot)
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var diagnostic = EditorSetupDiagnostics.Begin("recovery-write");
            EnsureDirectory();
            _currentSnapshotPath ??= GetSnapshotPath(snapshot.Pid, snapshot.Timestamp);
            var temporaryPath = _currentSnapshotPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 16_384, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    using var serialization = EditorSetupDiagnostics.Begin("recovery-serialize");
                    await RecoverySnapshotWriter.WriteAsync(stream, snapshot).ConfigureAwait(false);
                }
                // Readers discover only complete snapshots, and a failed write retains the last snapshot.
                File.Move(temporaryPath, _currentSnapshotPath, overwrite: true);
            }
            finally
            {
                try { File.Delete(temporaryPath); }
                catch { /* A cleanup failure must not hide the original write error. */ }
            }
        }
        finally { _writeGate.Release(); }
    }

    public async Task<IReadOnlyList<(string FilePath, SessionSnapshot Snapshot)>> ReadOrphanedSnapshotsAsync()
    {
        if (!Directory.Exists(_recoveryDir))
            return Array.Empty<(string, SessionSnapshot)>();

        var results = new List<(string, SessionSnapshot)>();
        foreach (var file in Directory.GetFiles(_recoveryDir, "session-*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var snapshot = JsonSerializer.Deserialize<SessionSnapshot>(json);
                if (snapshot == null) continue;

                // Check if the PID is still running
                if (IsPidRunning(snapshot.Pid)) continue;

                results.Add((file, snapshot));
            }
            catch { /* Skip corrupt files */ }
        }
        return results;
    }

    /// <summary>How long a snapshot left by a crashed instance stays recoverable before startup deletes it.</summary>
    public static readonly TimeSpan MaxOrphanAge = TimeSpan.FromDays(14);

    /// <summary>
    /// Orphaned snapshots from the last <see cref="MaxOrphanAge"/>, oldest first. Deletes older orphans, and
    /// orphans without any query text, so recovery files cannot pile up across crashes.
    /// </summary>
    public async Task<IReadOnlyList<(string FilePath, SessionSnapshot Snapshot)>> TakeRecoverableSnapshotsAsync(DateTimeOffset now)
    {
        var recoverable = new List<(string FilePath, SessionSnapshot Snapshot)>();
        foreach (var orphan in await ReadOrphanedSnapshotsAsync())
        {
            var hasText = orphan.Snapshot.Tabs?.Any(t => !string.IsNullOrWhiteSpace(t?.QueryText)) == true;
            if (hasText && now - orphan.Snapshot.Timestamp <= MaxOrphanAge)
                recoverable.Add(orphan);
            else
                await DeleteSnapshotAsync(orphan.FilePath);
        }
        return recoverable.OrderBy(o => o.Snapshot.Timestamp).ToList();
    }

    public Task DeleteSnapshotAsync(string filePath)
    {
        try { File.Delete(filePath); }
        catch { /* Ignore */ }
        return Task.CompletedTask;
    }

    public Task DeleteCurrentSnapshotAsync()
    {
        if (_currentSnapshotPath != null)
            return DeleteSnapshotAsync(_currentSnapshotPath);
        return Task.CompletedTask;
    }

    private static bool IsPidRunning(int pid)
    {
        try
        {
            var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
