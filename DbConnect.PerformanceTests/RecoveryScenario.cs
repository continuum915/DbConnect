using System.Text.Json;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.PerformanceTests;

public sealed record RecoveryObservations(string Attribution, long PersistedBytes, int TabCount,
    string Directory, string DriveType, string FileSystem, long AvailableFreeBytes, string WriteSemantics);

public sealed class RecoveryScenario : IDisposable
{
    public const string Id = "recovery.persist";
    private readonly string _directory;
    private readonly SessionSnapshot _snapshot;
    private readonly SessionRecoveryService _service;
    public RecoveryObservations Context { get; }

    public RecoveryScenario(ImportFixture fixture, string directory)
    {
        // The caller must assign a fresh location; never discover or restore existing sessions.
        if (Path.Exists(directory)) throw new IOException("Recovery measurement requires a new directory.");
        _directory = Path.GetFullPath(directory);
        System.IO.Directory.CreateDirectory(_directory);
        _service = new SessionRecoveryService(_directory);
        _snapshot = new SessionSnapshot
        {
            Pid = Environment.ProcessId,
            Timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            ActiveTabTitle = "Synthetic import",
            Tabs = [new TabSnapshot { Title = "Synthetic import", QueryText = fixture.Sql,
                SessionName = "Synthetic SQL Server", ConnectionId = Guid.Parse("11111111-1111-1111-1111-111111111111") },
                new TabSnapshot { Title = "Verification", QueryText = "SELECT 'recovery';\r\n" }]
        };
        var drive = new DriveInfo(Path.GetPathRoot(_directory)!);
        Context = new("combined serialization + directory check + temporary-file write; independent attribution unavailable through existing service interface",
            0, 2, _directory, drive.DriveType.ToString(), drive.DriveFormat, drive.AvailableFreeSpace,
            "Production indented UTF-8 JSON with bounded string segments, temporary-file streaming and rename publication; OS cache included, no durable flush; fresh file, no debounce/app scheduling.");
    }

    public Task ExecuteAsync() => _service.WriteSnapshotAsync(_snapshot);

    public async Task<RecoveryObservations> ValidateAsync()
    {
        var files = System.IO.Directory.GetFiles(_directory);
        if (files.Length != 1) throw new InvalidDataException("Expected exactly one recovery snapshot.");
        var loaded = JsonSerializer.Deserialize<SessionSnapshot>(await File.ReadAllTextAsync(files[0]));
        if (loaded is null || loaded.Pid != _snapshot.Pid || loaded.Timestamp != _snapshot.Timestamp ||
            loaded.ActiveTabTitle != _snapshot.ActiveTabTitle || loaded.Tabs.Count != _snapshot.Tabs.Count)
            throw new InvalidDataException("Recovery snapshot metadata differs.");
        for (var i = 0; i < loaded.Tabs.Count; i++)
        {
            var expected = _snapshot.Tabs[i];
            var actual = loaded.Tabs[i];
            if (actual.Title != expected.Title || actual.QueryText != expected.QueryText ||
                actual.SessionName != expected.SessionName || actual.ConnectionId != expected.ConnectionId)
                throw new InvalidDataException($"Recovery tab {i} differs.");
        }
        return Context with { PersistedBytes = new FileInfo(files[0]).Length };
    }

    public void Dispose()
    {
        if (System.IO.Directory.Exists(_directory)) System.IO.Directory.Delete(_directory, recursive: true);
        if (Path.Exists(_directory)) throw new IOException("Recovery directory cleanup failed.");
    }
}
