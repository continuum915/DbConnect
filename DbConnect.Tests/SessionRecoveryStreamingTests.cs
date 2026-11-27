using System.Text.Json;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public class SessionRecoveryStreamingTests
{
    [Fact]
    public async Task RewritesSameSnapshotPathAndRemainsReadableByLegacyRecovery()
    {
        var directory = NewDirectory();
        try
        {
            var service = new SessionRecoveryService(directory);
            var first = RecoverySnapshotWriterTests.CreateSnapshot(new string('x', 65_536));
            await service.WriteSnapshotAsync(first);
            var path = Assert.Single(Directory.GetFiles(directory));
            var latest = RecoverySnapshotWriterTests.CreateSnapshot("SELECT 'latest';\r\n");
            latest.Timestamp = latest.Timestamp.AddMinutes(1);

            await service.WriteSnapshotAsync(latest);

            Assert.Equal(path, Assert.Single(Directory.GetFiles(directory)));
            Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(latest, new JsonSerializerOptions { WriteIndented = true }),
                await File.ReadAllBytesAsync(path));
            var orphan = Assert.Single(await service.ReadOrphanedSnapshotsAsync());
            Assert.Equal(latest.Timestamp, orphan.Snapshot.Timestamp);
            Assert.Equal(latest.Tabs[0].QueryText, orphan.Snapshot.Tabs[0].QueryText);
            await service.DeleteCurrentSnapshotAsync();
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task OverlappingWritesPublishLatestCompleteSnapshotWithoutTemporaryFiles()
    {
        var directory = NewDirectory();
        try
        {
            var service = new SessionRecoveryService(directory);
            var first = service.WriteSnapshotAsync(RecoverySnapshotWriterTests.CreateSnapshot(new string('\n', 1_048_576)));
            var second = service.WriteSnapshotAsync(RecoverySnapshotWriterTests.CreateSnapshot("SELECT latest"));

            await Task.WhenAll(first, second);

            var path = Assert.Single(Directory.GetFiles(directory));
            var saved = JsonSerializer.Deserialize<SessionSnapshot>(await File.ReadAllTextAsync(path));
            Assert.Equal("SELECT latest", saved!.Tabs[0].QueryText);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task FailedPublishCleansTemporaryFileAndReleasesWriteGate()
    {
        var directory = NewDirectory();
        try
        {
            var service = new SessionRecoveryService(directory);
            await service.WriteSnapshotAsync(RecoverySnapshotWriterTests.CreateSnapshot("SELECT first"));
            var path = Assert.Single(Directory.GetFiles(directory));
            File.Delete(path);
            Directory.CreateDirectory(path); // An existing directory prevents publishing the replacement file.

            var error = await Record.ExceptionAsync(() => service.WriteSnapshotAsync(RecoverySnapshotWriterTests.CreateSnapshot("SELECT failed")));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());

            Assert.Empty(Directory.GetFiles(directory));
            Directory.Delete(path);
            await service.WriteSnapshotAsync(RecoverySnapshotWriterTests.CreateSnapshot("SELECT retry"));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DbConnect-recovery-stream-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
