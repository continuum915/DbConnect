using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using AvaloniaEdit;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.Tests;

/// <summary>These tests change the app-wide preferences, so they never run alongside other tests.</summary>
[CollectionDefinition("AppPreferences", DisableParallelization = true)]
public sealed class AppPreferencesCollection;

/// <summary>Tier 1 §1.4: the Preferences dialog and the behavior each page controls.</summary>
[Collection("AppPreferences")]
public sealed class PreferencesTests : MainWindowTestBase, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rain-prefs-{Guid.NewGuid():N}");

    public PreferencesTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        AppPreferences.Reset();
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    // ── 01: dialog, persistence, General ─────────────────────────────────────

    [Fact]
    public void SettingsWithoutPreferences_LoadTodaysBehavior()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"ThemeVariant\":\"Dark\"}")!;

        Assert.Equal(new EditorPreferences(), settings.Editor);
        Assert.Equal(new ResultPreferences(), settings.Results);
        Assert.Equal(["Production"], settings.Execution.ConfirmDataChangesOnTiers);
        Assert.False(settings.Execution.NewTabsManualCommit);
        Assert.Equal(14, settings.Editor.FontSize);
        Assert.False(settings.Editor.InsertSpaces);
    }

    [Fact]
    public async Task Apply_WritesSettings_SavesOnce_UpdatesPreferencesInEffect_AndSetsTheTheme()
    {
        var settings = new AppSettings();
        var saves = 0;
        string? theme = null;
        var changes = 0;
        void OnChanged() => changes++;
        AppPreferences.Changed += OnChanged;
        try
        {
            var vm = Create(settings, s => { saves++; return Task.CompletedTask; }, t => theme = t);
            vm.FontSize = 18;
            vm.NullText = "(null)";
            vm.NewTabsManualCommit = true;
            vm.ConfirmOnStaging = true;
            vm.Theme = "Dark";
            vm.RestorePreviousSession = false;
            vm.UpdateCheckUrl = " https://example.test/v.json ";

            Assert.True(await vm.Apply());

            Assert.Equal(18, settings.Editor.FontSize);
            Assert.Equal("(null)", settings.Results.NullText);
            Assert.True(settings.Execution.NewTabsManualCommit);
            Assert.Equal(["Staging", "Production"], settings.Execution.ConfirmDataChangesOnTiers);
            Assert.False(settings.RestorePreviousSession);
            Assert.Equal("https://example.test/v.json", settings.UpdateCheckUrl);
            Assert.Equal(1, saves);
            Assert.Equal("Dark", theme);
            Assert.Equal(1, changes);
            Assert.Equal(18, AppPreferences.Editor.FontSize);
            Assert.Equal("(null)", AppPreferences.Results.NullText);
        }
        finally { AppPreferences.Changed -= OnChanged; }
    }

    [Fact]
    public async Task OkCloses_CancelDiscards()
    {
        var settings = new AppSettings();
        var vm = Create(settings);
        bool? closed = null;
        vm.CloseRequested += ok => closed = ok;

        vm.FontSize = 20;
        vm.CancelCommand.Execute(null);
        Assert.False(closed);
        Assert.Equal(14, settings.Editor.FontSize);

        await vm.OkCommand.ExecuteAsync(null);
        Assert.True(closed);
        Assert.Equal(20, settings.Editor.FontSize);
    }

    [Theory]
    [InlineData(nameof(PreferencesViewModel.FontSize), 5, "Font size")]
    [InlineData(nameof(PreferencesViewModel.FontSize), 49, "Font size")]
    [InlineData(nameof(PreferencesViewModel.TabWidth), 0, "Tab width")]
    public async Task OutOfRangeNumbers_AreRejected(string property, int value, string message)
    {
        var settings = new AppSettings();
        var vm = Create(settings);
        typeof(PreferencesViewModel).GetProperty(property)!.SetValue(vm, value);

        Assert.False(vm.IsValid);
        Assert.Contains(message, vm.ValidationError);
        Assert.False(await vm.Apply());
        Assert.Equal(new EditorPreferences(), settings.Editor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("%")]
    public void InvalidDateFormats_AreRejected(string format)
    {
        var vm = Create(new AppSettings());
        vm.DateTimeFormat = format;

        Assert.False(vm.IsValid);
        Assert.Equal(string.Empty, vm.DateTimeSample);
    }

    [Fact]
    public void ResetPage_RestoresOnlyThatPagesDefaults()
    {
        var vm = Create(new AppSettings());
        vm.FontSize = 22;
        vm.NullText = "(null)";

        vm.SelectedPage = PreferencesViewModel.EditorPage;
        vm.ResetPageCommand.Execute(null);

        Assert.Equal(14, vm.FontSize);
        Assert.Equal("(null)", vm.NullText);
    }

    [Fact]
    public async Task TimeoutAndRowCapDefaults_AreTheWorkspaceDefaultsForNewTabs()
    {
        var workspace = new QueryWorkspaceService(Path.Combine(_directory, "query-workspace.json"));
        var vm = new PreferencesViewModel(new AppSettings(), "Light", workspace, _ => { }, _ => Task.CompletedTask);
        await vm.LoadAsync();
        vm.DefaultTimeoutSeconds = 120;
        vm.DefaultRowCap = 5000;

        await vm.Apply();

        Assert.Equal(120, await workspace.LoadTimeoutSecondsAsync());
        Assert.Equal(5000, await workspace.LoadResultRowCapAsync());
    }

    [Fact]
    public async Task ChangingATabsTimeoutOrRowCap_DoesNotChangeTheDefault()
    {
        var workspace = new QueryWorkspaceService(Path.Combine(_directory, "query-workspace.json"));
        await workspace.SaveTimeoutSecondsAsync(60);
        await workspace.SaveResultRowCapAsync(500);
        var tab = new QueryTabViewModel("Q", workspaceService: workspace);
        for (var i = 0; i < 50 && tab.TimeoutSeconds != 60; i++) await Task.Delay(10);

        tab.TimeoutSeconds = 300;
        tab.ResultRowCap = 50_000;
        await Task.Delay(50);

        Assert.Equal(60, await workspace.LoadTimeoutSecondsAsync());
        Assert.Equal(500, await workspace.LoadResultRowCapAsync());
    }

    // ── 02: editor ───────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void OpenEditors_FollowEditorPreferences()
    {
        var view = new QueryTabView { DataContext = new QueryTabViewModel() };
        var window = new Window { Content = view };
        try
        {
            window.Show();
            var editor = view.FindControl<TextEditor>("QueryEditor")!;
            Assert.Equal(14, editor.FontSize);
            Assert.False(editor.WordWrap);

            AppPreferences.Apply(new AppSettings
            {
                Editor = new EditorPreferences { FontFamily = "Courier New", FontSize = 20, TabWidth = 2, InsertSpaces = true, WordWrap = true }
            });

            Assert.Equal(20, editor.FontSize);
            Assert.Equal("Courier New", editor.FontFamily.Name);
            Assert.Equal(2, editor.Options.IndentationSize);
            Assert.True(editor.Options.ConvertTabsToSpaces);
            Assert.True(editor.WordWrap);
        }
        finally { window.Close(); }
    }

    // ── 03: result display ───────────────────────────────────────────────────

    [Fact]
    public void GridDisplay_FollowsResultPreferences()
    {
        var display = new ResultPreferences { NullText = "(null)", DateTimeFormat = "dd/MM/yyyy", BinaryDisplay = BinaryDisplayMode.Hex };

        Assert.Equal("(null)", GridCellDisplay.FormatValue(null, display));
        Assert.Equal("(null)", GridCellDisplay.FormatValue(DBNull.Value, display));
        Assert.Equal("08/11/2026", GridCellDisplay.FormatValue(new DateTime(2026, 11, 8, 14, 5, 9), display));
        Assert.Equal("08/11/2026 +02:00", GridCellDisplay.FormatValue(new DateTimeOffset(2026, 11, 8, 14, 5, 9, TimeSpan.FromHours(2)), display));
        Assert.Equal("0xCAFE", GridCellDisplay.FormatValue(new byte[] { 0xCA, 0xFE }, display));
        Assert.Equal("0x" + new string('A', 64) + "…", GridCellDisplay.FormatValue(Enumerable.Repeat((byte)0xAA, 40).ToArray(), display));
        Assert.Equal("<2 bytes>", GridCellDisplay.FormatValue(new byte[] { 1, 2 }, new ResultPreferences()));
    }

    [Fact]
    public void DisplayPreferences_ChangeGrids_ButNotCopyExportOrSqlLiterals()
    {
        var date = new DateTime(2026, 11, 8, 14, 5, 9);
        var copyBefore = QueryResultFormattingService.FormatValue(null) + QueryResultFormattingService.FormatValue(date);
        var literalBefore = SqlLiteral.Format(null) + SqlLiteral.Format(date) + SqlLiteral.Format(new byte[] { 1 });

        AppPreferences.Apply(new AppSettings
        {
            Results = new ResultPreferences { NullText = "(null)", DateTimeFormat = "dd/MM/yyyy", BinaryDisplay = BinaryDisplayMode.Hex }
        });

        Assert.Equal("(null)", GridCellDisplay.FormatValue(null));
        Assert.Equal(copyBefore, QueryResultFormattingService.FormatValue(null) + QueryResultFormattingService.FormatValue(date));
        Assert.Equal(literalBefore, SqlLiteral.Format(null) + SqlLiteral.Format(date) + SqlLiteral.Format(new byte[] { 1 }));
    }

    // ── 04: execution ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("SELECT * FROM t", true)]
    [InlineData("  -- note\n select 1;", true)]
    [InlineData("(SELECT 1) UNION (SELECT 2)", true)]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x", true)]
    [InlineData("VALUES (1), (2)", true)]
    [InlineData("SHOW TABLES", true)]
    [InlineData("DESCRIBE t", true)]
    [InlineData("EXPLAIN SELECT * FROM t", true)]
    [InlineData("EXPLAIN ANALYZE SELECT * FROM t", true)]
    [InlineData("PRAGMA table_info(t)", true)]
    [InlineData("BEGIN", true)]
    [InlineData("COMMIT", true)]
    [InlineData("SET search_path TO app", true)]
    [InlineData("SELECT 'DELETE FROM t'", true)]
    [InlineData("INSERT INTO t VALUES (1)", false)]
    [InlineData("update t set a = 1", false)]
    [InlineData("DELETE FROM t", false)]
    [InlineData("MERGE INTO t USING s ON 1=1 WHEN MATCHED THEN DELETE", false)]
    [InlineData("TRUNCATE TABLE t", false)]
    [InlineData("DROP TABLE t", false)]
    [InlineData("CREATE TABLE t (a int)", false)]
    [InlineData("SELECT * INTO copy FROM t", false)]
    [InlineData("WITH d AS (DELETE FROM t RETURNING *) SELECT * FROM d", false)]
    [InlineData("EXPLAIN ANALYZE DELETE FROM t", false)]
    [InlineData("PRAGMA foreign_keys = OFF", false)]
    [InlineData("SET GLOBAL max_connections = 10", false)]
    [InlineData("EXEC dbo.Purge", false)]
    [InlineData("VACUUM", false)]
    public void Classifier_PassesOnlyKnownReadOnlyStatements(string sql, bool readOnly) =>
        Assert.Equal(readOnly, DataChangeClassifier.IsReadOnly(sql, SqlBatchDialect.Standard));

    [Fact]
    public void Classifier_FindsTheFirstChangeInABatch()
    {
        Assert.Null(DataChangeClassifier.FirstDataChange("SELECT 1; SELECT 2;", SqlBatchDialect.Standard));
        Assert.Equal("DELETE FROM t", DataChangeClassifier.FirstDataChange("SELECT 1;\nDELETE FROM t;\nINSERT INTO t VALUES (1);", SqlBatchDialect.Standard)?.TrimEnd(';'));
    }

    [Theory]
    [InlineData("Development", "DELETE FROM t", false)]
    [InlineData("Production", "SELECT 1", false)]
    [InlineData("Production", "DELETE FROM t", true)]
    public async Task Safety_AsksOnlyForChangesOnListedTiers(string tier, string sql, bool asks)
    {
        var safety = new ExecutionSafety();
        DataChangeConfirmation? asked = null;
        safety.ConfirmDataChange = c => { asked = c; return Task.FromResult(true); };

        Assert.True(await safety.ConfirmRunAsync(Session(tier), sql, CancellationToken.None));

        Assert.Equal(asks, asked != null);
        if (asked != null)
        {
            Assert.Equal("Production", asked.EnvironmentTier);
            Assert.StartsWith("DELETE FROM t", asked.Statement);
        }
    }

    [Fact]
    public async Task Safety_WithoutAWayToAsk_DoesNotRunChanges()
    {
        Assert.False(await new ExecutionSafety().ConfirmRunAsync(Session("Production"), "DELETE FROM t", CancellationToken.None));
    }

    [Fact]
    public async Task DecliningTheConfirmation_RunsNothing()
    {
        var file = Path.Combine(_directory, "prod.db");
        var service = new DatabaseService();
        var session = service.CreateSession(new ConnectionInfo
        {
            Name = "prod",
            Provider = "SQLite",
            ConnectionString = $"Data Source={file}",
            EnvironmentTier = "Production"
        });
        var vm = CreateMainWindowViewModel(service);
        vm.ActiveSessionOptions.Add(session);
        var tab = vm.NewQueryTabWithSession(session);
        var asked = 0;
        vm.Safety.ConfirmDataChange = _ => { asked++; return Task.FromResult(asked == 1); };

        await Run(vm, tab, "CREATE TABLE items (id INTEGER); INSERT INTO items VALUES (1);");
        await Run(vm, tab, "INSERT INTO items VALUES (2);");
        await Run(vm, tab, "SELECT COUNT(*) FROM items;");

        Assert.Equal(2, asked);
        Assert.Equal(1L, Convert.ToInt64(tab.CurrentResult!.ObjectRows[0][0]));
    }

    [Fact]
    public void NewTabs_StartInManualCommit_WhenPreferred()
    {
        Assert.False(new QueryTabViewModel("Q").IsManualCommit);

        AppPreferences.Apply(new AppSettings { Execution = new ExecutionPreferences { NewTabsManualCommit = true } });

        Assert.True(new QueryTabViewModel("Q").IsManualCommit);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static PreferencesViewModel Create(AppSettings settings, Func<AppSettings, Task>? save = null, Action<string>? setTheme = null) =>
        new(settings, "Light", workspace: null, setTheme ?? (_ => { }), save ?? (_ => Task.CompletedTask));

    private static DatabaseSession Session(string tier) =>
        new DatabaseService().CreateSession(new ConnectionInfo
        {
            Name = "conn",
            Provider = "SQLite",
            ConnectionString = "Data Source=:memory:",
            EnvironmentTier = tier
        });

    private static async Task Run(MainWindowViewModel vm, QueryTabViewModel tab, string sql)
    {
        vm.SelectedTab = tab;
        tab.QueryText = sql;
        await vm.ExecuteQueryCommand.ExecuteAsync(null);
    }
}
