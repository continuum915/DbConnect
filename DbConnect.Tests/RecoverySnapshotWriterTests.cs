using System.Text.Json;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public class RecoverySnapshotWriterTests
{
    [Fact]
    public async Task MatchesLegacyJsonBytesIncludingEscapingNullsAndSegmentBoundaries()
    {
        string?[] values = [null, "", "SELECT 'quoted';\r\n\t<>&\"\\ café 😀",
            new string('x', RecoverySnapshotWriter.StringSegmentCharacters - 1) + "😀" + new string('\n', 32_768),
            new string('x', RecoverySnapshotWriter.StringSegmentCharacters - 1) + new string('\ud800', 1) + "tail",
            new string('x', RecoverySnapshotWriter.StringSegmentCharacters) + new string('\udc00', 1),
            new string('\ud800', 1) + " invalid " + new string('\udc00', 1)];
        foreach (var value in values)
        {
            var snapshot = CreateSnapshot(value);
            await using var output = new MemoryStream();

            await RecoverySnapshotWriter.WriteAsync(output, snapshot);

            var expected = JsonSerializer.SerializeToUtf8Bytes(snapshot, new JsonSerializerOptions { WriteIndented = true });
            Assert.Equal(expected, output.ToArray());
        }
    }

    [Fact]
    public async Task PreservesNullTabCollectionAndNullTabEntries()
    {
        foreach (var tabs in new List<TabSnapshot>?[] { null, [], [null!] })
        {
            var snapshot = CreateSnapshot("SELECT 1");
            snapshot.Tabs = tabs!;
            await using var output = new MemoryStream();

            await RecoverySnapshotWriter.WriteAsync(output, snapshot);

            Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(snapshot, new JsonSerializerOptions { WriteIndented = true }), output.ToArray());
        }
    }

    [Fact]
    public async Task LargeEscapedStringUsesBoundedBuffersAndProgressiveWrites()
    {
        var snapshot = CreateSnapshot(new string('\u0001', 8 * 1_048_576));
        using var output = new CountingStream();
        var before = GC.GetAllocatedBytesForCurrentThread();

        var writing = RecoverySnapshotWriter.WriteAsync(output, snapshot);
        // This stream completes every I/O synchronously, keeping the allocation counter on one thread.
        Assert.True(writing.IsCompletedSuccessfully);
        await writing;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(output.BytesWritten >= 8L * 1_048_576 * 6);
        Assert.True(output.WriteCount >= 512);
        Assert.InRange(output.MaximumWriteBytes, 1, 128 * 1024);
        Assert.True(allocated < 2 * 1_048_576, $"Streaming allocated {allocated:N0} bytes.");
    }

    internal static SessionSnapshot CreateSnapshot(string? sql) => new()
    {
        Pid = int.MaxValue,
        Timestamp = new DateTimeOffset(2026, 11, 2, 10, 20, 30, TimeSpan.FromHours(-5)).AddTicks(1234567),
        ActiveTabTitle = null,
        Tabs = [new TabSnapshot { Title = "Query \"1\"", QueryText = sql!, SessionName = "Local",
            ConnectionId = Guid.Parse("11111111-2222-3333-4444-555555555555") },
            new TabSnapshot { Title = null!, QueryText = "", SessionName = null, ConnectionId = null }]
    };

    private sealed class CountingStream : Stream
    {
        internal long BytesWritten { get; private set; }
        internal int WriteCount { get; private set; }
        internal int MaximumWriteBytes { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Record(count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Record(buffer.Length);
            return ValueTask.CompletedTask;
        }
        private void Record(int count)
        {
            BytesWritten += count;
            WriteCount++;
            MaximumWriteBytes = Math.Max(MaximumWriteBytes, count);
        }
    }
}
