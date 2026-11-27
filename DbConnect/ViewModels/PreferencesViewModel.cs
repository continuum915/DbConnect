using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>
/// The Preferences dialog: Editor, Results, Execution and General pages over copies of the settings.
/// Apply writes them to the shared settings object, saves, and updates <see cref="AppPreferences"/>;
/// Cancel discards edits; Reset restores the selected page's defaults.
/// </summary>
public sealed partial class PreferencesViewModel : ObservableObject
{
    public const string EditorPage = "Editor";
    public const string ResultsPage = "Results";
    public const string ExecutionPage = "Execution";
    public const string GeneralPage = "General";

    private readonly AppSettings _settings;
    private readonly QueryWorkspaceService? _workspace;
    private readonly Action<string> _setTheme;
    private readonly Func<AppSettings, Task> _saveSettings;

    public PreferencesViewModel(
        AppSettings settings,
        string currentTheme,
        QueryWorkspaceService? workspace,
        Action<string> setTheme,
        Func<AppSettings, Task> saveSettings)
    {
        _settings = settings;
        _workspace = workspace;
        _setTheme = setTheme;
        _saveSettings = saveSettings;
        LoadEditor(settings.Editor ?? new());
        LoadResults(settings.Results ?? new());
        LoadExecution(settings.Execution ?? new());
        _theme = currentTheme;
        _restorePreviousSession = settings.RestorePreviousSession;
        _updateCheckUrl = settings.UpdateCheckUrl;
    }

    public IReadOnlyList<string> Pages { get; } = [EditorPage, ResultsPage, ExecutionPage, GeneralPage];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditorPage), nameof(IsResultsPage), nameof(IsExecutionPage), nameof(IsGeneralPage))]
    private string _selectedPage = EditorPage;

    public bool IsEditorPage => SelectedPage == EditorPage;
    public bool IsResultsPage => SelectedPage == ResultsPage;
    public bool IsExecutionPage => SelectedPage == ExecutionPage;
    public bool IsGeneralPage => SelectedPage == GeneralPage;

    /// <summary>Raised with true after OK applied the changes, or false for Cancel.</summary>
    public event Action<bool>? CloseRequested;

    // ── Editor ────────────────────────────────────────────────────────────────

    [ObservableProperty][NotifyPropertyChangedFor(nameof(ValidationError), nameof(IsValid))] private string _fontFamily = string.Empty;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ValidationError), nameof(IsValid))] private int _fontSize;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ValidationError), nameof(IsValid))] private int _tabWidth;
    [ObservableProperty] private bool _insertSpaces;
    [ObservableProperty] private bool _wordWrap;
    [ObservableProperty] private bool _autoOpenCompletion;

    // ── Results ───────────────────────────────────────────────────────────────

    [ObservableProperty] private string _nullText = string.Empty;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ValidationError), nameof(IsValid), nameof(DateTimeSample))] private string _dateTimeFormat = string.Empty;
    [ObservableProperty] private BinaryDisplayMode _binaryDisplay;
    [ObservableProperty] private decimal? _maxResultRows = 1_000_000;

    public IReadOnlyList<BinaryDisplayMode> BinaryDisplayModes { get; } = Enum.GetValues<BinaryDisplayMode>();

    /// <summary>The current time in the chosen format, or empty while the format is invalid.</summary>
    public string DateTimeSample => TryFormatDate(DateTimeFormat, out var sample) ? sample : string.Empty;

    // ── Execution ─────────────────────────────────────────────────────────────

    public IReadOnlyList<int> TimeoutChoices { get; } = [15, 30, 60, 120, 300];
    public IReadOnlyList<int> RowCapChoices { get; } = [100, 250, 500, 1000, 5000, 50_000];

    [ObservableProperty] private int _defaultTimeoutSeconds = 30;
    [ObservableProperty] private int _defaultRowCap = 1000;
    [ObservableProperty] private bool _newTabsManualCommit;
    [ObservableProperty] private bool _confirmOnDevelopment;
    [ObservableProperty] private bool _confirmOnStaging;
    [ObservableProperty] private bool _confirmOnProduction;

    // ── General ───────────────────────────────────────────────────────────────

    public IReadOnlyList<string> ThemeOptions { get; } = ["Light", "Dark", "System"];

    [ObservableProperty] private string _theme;
    [ObservableProperty] private bool _restorePreviousSession;
    [ObservableProperty] private string _updateCheckUrl;

    // ── Validation ────────────────────────────────────────────────────────────

    public string? ValidationError =>
        string.IsNullOrWhiteSpace(FontFamily) ? "Enter a font family."
        : FontSize is < 6 or > 48 ? "Font size must be between 6 and 48."
        : TabWidth is < 1 or > 16 ? "Tab width must be between 1 and 16."
        : !TryFormatDate(DateTimeFormat, out _) ? "Enter a valid date/time format, such as yyyy-MM-dd HH:mm:ss."
        : null;

    public bool IsValid => ValidationError == null;

    /// <summary>Loads the defaults stored in the query workspace (timeout and row cap for new tabs).</summary>
    public async Task LoadAsync()
    {
        if (_workspace == null) return;
        DefaultTimeoutSeconds = Choice(await _workspace.LoadTimeoutSecondsAsync(), TimeoutChoices, 30);
        DefaultRowCap = Choice(await _workspace.LoadResultRowCapAsync(), RowCapChoices, 1000);
    }

    [RelayCommand]
    public async Task<bool> Apply()
    {
        if (!IsValid) return false;

        _settings.Editor = new EditorPreferences
        {
            FontFamily = FontFamily.Trim(),
            FontSize = FontSize,
            TabWidth = TabWidth,
            InsertSpaces = InsertSpaces,
            WordWrap = WordWrap,
            AutoOpenCompletion = AutoOpenCompletion
        };
        _settings.Results = new ResultPreferences
        {
            NullText = NullText,
            DateTimeFormat = DateTimeFormat,
            BinaryDisplay = BinaryDisplay,
            MaxResultRows = (int)Math.Clamp(MaxResultRows ?? 1_000_000, 1_000, 100_000_000)
        };
        _settings.Execution = new ExecutionPreferences
        {
            NewTabsManualCommit = NewTabsManualCommit,
            ConfirmDataChangesOnTiers = new[] { ("Development", ConfirmOnDevelopment), ("Staging", ConfirmOnStaging), ("Production", ConfirmOnProduction) }
                .Where(tier => tier.Item2).Select(tier => tier.Item1).ToList()
        };
        _settings.RestorePreviousSession = RestorePreviousSession;
        _settings.UpdateCheckUrl = UpdateCheckUrl.Trim();

        if (_workspace != null)
        {
            await _workspace.SaveTimeoutSecondsAsync(DefaultTimeoutSeconds);
            await _workspace.SaveResultRowCapAsync(DefaultRowCap);
        }
        AppPreferences.Apply(_settings);
        _setTheme(Theme);
        await _saveSettings(_settings);
        return true;
    }

    [RelayCommand]
    private async Task Ok()
    {
        if (await Apply()) CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);

    /// <summary>Restores the selected page's defaults; nothing is saved until Apply or OK.</summary>
    [RelayCommand]
    private void ResetPage()
    {
        switch (SelectedPage)
        {
            case EditorPage: LoadEditor(new()); break;
            case ResultsPage: LoadResults(new()); break;
            case ExecutionPage:
                LoadExecution(new());
                DefaultTimeoutSeconds = 30;
                DefaultRowCap = 1000;
                break;
            case GeneralPage:
                Theme = "Light";
                RestorePreviousSession = true;
                UpdateCheckUrl = string.Empty;
                break;
        }
    }

    private void LoadEditor(EditorPreferences editor)
    {
        FontFamily = editor.FontFamily;
        FontSize = editor.FontSize;
        TabWidth = editor.TabWidth;
        InsertSpaces = editor.InsertSpaces;
        WordWrap = editor.WordWrap;
        AutoOpenCompletion = editor.AutoOpenCompletion;
    }

    private void LoadResults(ResultPreferences results)
    {
        NullText = results.NullText;
        DateTimeFormat = results.DateTimeFormat;
        BinaryDisplay = results.BinaryDisplay;
        MaxResultRows = results.MaxResultRows;
    }

    private void LoadExecution(ExecutionPreferences execution)
    {
        NewTabsManualCommit = execution.NewTabsManualCommit;
        var tiers = execution.ConfirmDataChangesOnTiers ?? [];
        ConfirmOnDevelopment = tiers.Contains("Development");
        ConfirmOnStaging = tiers.Contains("Staging");
        ConfirmOnProduction = tiers.Contains("Production");
    }

    private static int Choice(int value, IReadOnlyList<int> choices, int fallback) => choices.Contains(value) ? value : fallback;

    private static bool TryFormatDate(string format, out string sample)
    {
        sample = string.Empty;
        if (string.IsNullOrWhiteSpace(format)) return false;
        try
        {
            sample = new DateTime(2026, 11, 8, 14, 5, 9).ToString(format, CultureInfo.InvariantCulture);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
