using System.Text;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>File-backed SQL issue 02: file-bound query tabs with Save, Save As and Save All.</summary>
public sealed class SqlFileTests : MainWindowTestBase, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rain-sqlfile-{Guid.NewGuid():N}");
    private readonly Queue<string?> _savePaths = new();
    private readonly List<string> _errors = [];
    private bool _overwrite;
    private int _overwriteAsked;

    public SqlFileTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_directory))
            File.SetAttributes(file, FileAttributes.Normal);
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    // ── File service ──────────────────────────────────────────────────────────

    public static TheoryData<string, byte[]> Encodings() => new()
    {
        { "utf-8", [] },
        { "utf-8-bom", [0xEF, 0xBB, 0xBF] },
        { "utf-16le", [0xFF, 0xFE] },
    };

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task EditingAndSaving_KeepsTheEncodingAndBom(string name, byte[] bom)
    {
        var encoding = name == "utf-16le" ? Encoding.Unicode : new UTF8Encoding(false);
        var path = Write("e.sql", [.. bom, .. encoding.GetBytes("SELECT 'é';")]);

        var (text, document) = await SqlFileService.ReadAsync(path);
        Assert.Equal("SELECT 'é';", text);
        await SqlFileService.WriteAsync(path, "SELECT 'ü';", document);

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(bom, bytes.Take(bom.Length));
        Assert.Equal(encoding.GetBytes("SELECT 'ü';"), bytes.Skip(bom.Length));
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public async Task Saving_KeepsTheFilesLineEnding(string newLine)
    {
        var path = Write("n.sql", Encoding.UTF8.GetBytes($"SELECT 1;{newLine}SELECT 2;{newLine}"));
        var (text, document) = await SqlFileService.ReadAsync(path);

        // The editor may insert a different line ending; the file's own style wins on save.
        await SqlFileService.WriteAsync(path, text + "SELECT 3;\r\nSELECT 4;\n", document);

        Assert.Equal($"SELECT 1;{newLine}SELECT 2;{newLine}SELECT 3;{newLine}SELECT 4;{newLine}", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task InvalidUtf8_OpensWithTheFallbackEncoding_AndSavesTheSameBytes()
    {
        byte[] original = [0x53, 0x45, 0x4C, 0x45, 0x43, 0x54, 0x20, 0x27, 0xE9, 0x27];
        var path = Write("legacy.sql", original);

        var (text, document) = await SqlFileService.ReadAsync(path);

        Assert.NotNull(document.Notice);
        Assert.Contains("not valid UTF-8", document.Notice);
        Assert.StartsWith("SELECT '", text);
        await SqlFileService.WriteAsync(path, text, document);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task FailedWrite_LeavesTheOriginalFileAndNoTemporaryFile()
    {
        if (!OperatingSystem.IsWindows()) return; // read-only files block replacement on Windows only
        var path = Write("locked.sql", Encoding.UTF8.GetBytes("SELECT 1;"));
        var (_, document) = await SqlFileService.ReadAsync(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        await Assert.ThrowsAnyAsync<Exception>(() => SqlFileService.WriteAsync(path, "SELECT 2;", document));

        Assert.Equal("SELECT 1;", await File.ReadAllTextAsync(path));
        Assert.Single(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public async Task ChangesOnDisk_AreDetected()
    {
        var path = Write("c.sql", Encoding.UTF8.GetBytes("SELECT 1;"));
        var (_, document) = await SqlFileService.ReadAsync(path);
        Assert.False(SqlFileService.HasChangedOnDisk(document));

        await File.WriteAllTextAsync(path, "SELECT 22;");

        Assert.True(SqlFileService.HasChangedOnDisk(document));
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenEditSave_WritesTheBoundFile()
    {
        var vm = Create();
        var path = Write("report.sql", Encoding.UTF8.GetBytes("SELECT 1;"));

        await vm.Files.OpenFilesAsync([path]);
        var tab = Assert.IsType<QueryTabViewModel>(vm.SelectedTab);
        Assert.Equal("report.sql", tab.Title);
        Assert.Equal(path, tab.TabToolTip);
        Assert.False(tab.IsDirty);

        tab.QueryText = "SELECT 2;";
        Assert.True(tab.IsDirty);
        await vm.Files.SaveActiveCommand.ExecuteAsync(null);

        Assert.Equal("SELECT 2;", await File.ReadAllTextAsync(path));
        Assert.False(tab.IsDirty);
        Assert.Empty(_savePaths);
    }

    [Fact]
    public async Task SavingAnUntitledTab_AsksForAPath_AndBindsTheTab()
    {
        var vm = Create();
        var tab = vm.NewQueryTabWithContent("SELECT 1;");
        tab.QueryText = "SELECT 9;";
        var path = Path.Combine(_directory, "new.sql");
        _savePaths.Enqueue(path);

        await vm.Files.SaveActiveCommand.ExecuteAsync(null);

        Assert.Equal("SELECT 9;", await File.ReadAllTextAsync(path));
        Assert.Equal(path, tab.Document!.Path);
        Assert.Equal("new.sql", tab.Title);
        Assert.False(tab.IsDirty);
    }

    [Fact]
    public async Task SaveAs_WritesTheNewPath_AndRebindsWithoutTouchingTheOldFile()
    {
        var vm = Create();
        var oldPath = Write("old.sql", Encoding.UTF8.GetBytes("SELECT 1;"));
        await vm.Files.OpenFilesAsync([oldPath]);
        var tab = (QueryTabViewModel)vm.SelectedTab!;
        tab.QueryText = "SELECT 2;";
        var newPath = Path.Combine(_directory, "copy.sql");
        _savePaths.Enqueue(newPath);

        await vm.Files.SaveActiveAsCommand.ExecuteAsync(null);

        Assert.Equal("SELECT 1;", await File.ReadAllTextAsync(oldPath));
        Assert.Equal("SELECT 2;", await File.ReadAllTextAsync(newPath));
        Assert.Equal("copy.sql", tab.Title);
    }

    [Fact]
    public async Task SaveAll_SavesEveryUnsavedTab_AndStopsWhenAPathIsCancelled()
    {
        var vm = Create();
        var a = Write("a.sql", Encoding.UTF8.GetBytes("A"));
        await vm.Files.OpenFilesAsync([a]);
        ((QueryTabViewModel)vm.SelectedTab!).QueryText = "A2";
        var untitled = vm.NewQueryTabWithContent("B");
        untitled.QueryText = "B2";
        var clean = vm.NewQueryTabWithContent("C");
        _savePaths.Enqueue(null);

        await vm.Files.SaveAllCommand.ExecuteAsync(null);

        Assert.Equal("A2", await File.ReadAllTextAsync(a));
        Assert.True(untitled.IsDirty);
        Assert.False(clean.IsDirty);
    }

    [Fact]
    public async Task OpeningAFileThatIsAlreadyOpen_SelectsItsTab()
    {
        var vm = Create();
        var path = Write("same.sql", Encoding.UTF8.GetBytes("SELECT 1;"));
        await vm.Files.OpenFilesAsync([path]);
        var first = vm.SelectedTab;
        vm.NewQueryTabWithContent("other");

        await vm.Files.OpenFilesAsync([OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path]);

        Assert.Same(first, vm.SelectedTab);
        Assert.Equal(2, vm.Tabs.Count);
    }

    [Fact]
    public async Task OpeningSeveralFiles_OpensOneTabEach_ReusingAnUntouchedStarterTab()
    {
        var vm = Create();
        var starter = vm.NewQueryTabWithSession();
        var a = Write("a.sql", Encoding.UTF8.GetBytes("A"));
        var b = Write("b.sql", Encoding.UTF8.GetBytes("B"));

        await vm.Files.OpenFilesAsync([a, b]);

        Assert.Equal(2, vm.Tabs.Count);
        Assert.Same(starter, vm.Tabs[0]);
        Assert.Equal(["a.sql", "b.sql"], vm.Tabs.Select(t => t.Title));
    }

    [Fact]
    public async Task SavingOverAFileChangedOnDisk_AsksFirst_AndDecliningKeepsTheirVersion()
    {
        var vm = Create();
        var path = Write("shared.sql", Encoding.UTF8.GetBytes("SELECT 1;"));
        await vm.Files.OpenFilesAsync([path]);
        var tab = (QueryTabViewModel)vm.SelectedTab!;
        tab.QueryText = "SELECT mine;";
        await File.WriteAllTextAsync(path, "SELECT theirs;");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        await vm.Files.SaveActiveCommand.ExecuteAsync(null);

        Assert.Equal(1, _overwriteAsked);
        Assert.Equal("SELECT theirs;", await File.ReadAllTextAsync(path));
        Assert.True(tab.IsDirty);

        _overwrite = true;
        await vm.Files.SaveActiveCommand.ExecuteAsync(null);
        Assert.Equal("SELECT mine;", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task OpenFailure_IsReported()
    {
        var vm = Create();

        await vm.Files.OpenFilesAsync([Path.Combine(_directory, "missing.sql")]);

        Assert.Contains("Could not open", Assert.Single(_errors));
    }

    [Fact]
    public void EditsMadeWhileSaving_StayUnsaved()
    {
        var tab = new QueryTabViewModel { QueryText = "SELECT 1;" };
        var saved = tab.CurrentQueryTextSnapshot;
        var revision = tab.QueryTextRevision;
        tab.QueryText = "SELECT 1; -- typed during save";

        tab.ClearDirty(saved, revision);

        Assert.True(tab.IsDirty);
        tab.QueryText = "SELECT 1;";
        Assert.False(tab.IsDirty);
    }

    private MainWindowViewModel Create()
    {
        var vm = CreateMainWindowViewModel();
        vm.Tabs.Clear();
        vm.Files.PickSavePath = _ => Task.FromResult(_savePaths.Count > 0 ? _savePaths.Dequeue() : null);
        vm.Files.ConfirmOverwrite = _ => { _overwriteAsked++; return Task.FromResult(_overwrite); };
        vm.Files.ReportError = message => { _errors.Add(message); return Task.CompletedTask; };
        return vm;
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
