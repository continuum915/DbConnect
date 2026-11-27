using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public enum DataTransferStep { Source, Target, Objects, Options, Preview, Run }

/// <summary>
/// Tools ▸ Data Transfer…, modeled on Navicat's wizard: pick the source connection, the target connection and the
/// tables, then run. The transfer (<see cref="DataTransferJob"/>) runs in the background on sessions of its own, shows
/// each table's progress, can be canceled, and ends with each table's outcome.
/// </summary>
public sealed partial class DataTransferViewModel : ViewModelBase, ITableChoices
{
    private CancellationTokenSource? _cancellation;
    private DatabaseSession? _tablesLoadedFor;

    public DataTransferViewModel(ObservableCollection<DatabaseSession> availableSessions, DataTransferProfiles? profiles = null)
    {
        AvailableSessions = availableSessions;
        Title = "Data Transfer";
        Profiles = new(this, profiles ?? new DataTransferProfiles());
        _ = LoadProfileNamesAsync();
        SourceDatabase = new(OnDatabaseChanged);
        TargetDatabase = new(OnDatabaseChanged);
    }

    public DataTransferProfilesViewModel Profiles { get; }

    private async Task LoadProfileNamesAsync()
    {
        try { await Profiles.RefreshAsync(); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Profiles.Message = $"Saved profiles could not be read: {ex.Message}";
        }
    }

    /// <summary>The transfer as chosen so far, to save; null until a source, a target and tables are chosen.</summary>
    public DataTransferProfile? ToProfile(string name)
    {
        if (SourceSession is not { } source || TargetSession is not { } target || !Tables.Any(t => t.IsSelected)) return null;
        DataTransferEndpoint Side(DatabaseSession connection, DataTransferDatabasePicker picker) => new()
        {
            ConnectionId = connection.Connection.Id,
            ConnectionName = connection.Name,
            Database = picker.Session?.DatabaseLabel
        };
        var options = BuildOptions();
        return new DataTransferProfile
        {
            Name = name,
            Source = Side(source, SourceDatabase),
            Target = Side(target, TargetDatabase),
            Tables = Tables.Where(t => t.IsSelected).Select(t => new DataTransferProfileTable
            {
                Schema = t.Identity.Schema,
                Name = t.Identity.Name,
                Mode = (t as DataTransferTableChoice)?.Mode.Mode
            }).ToList(),
            BatchSize = options.BatchSize,
            Transaction = options.Transaction,
            ContinueOnError = options.ContinueOnError,
            Mode = options.Mode,
            IncludeIndexes = options.IncludeIndexes,
            IncludeForeignKeys = options.IncludeForeignKeys
        };
    }

    /// <summary>Fills every step from <paramref name="profile"/> and opens Options; says why not when a connection is not open.</summary>
    public async Task<string?> ApplyProfileAsync(DataTransferProfile profile)
    {
        DatabaseSession? Find(DataTransferEndpoint side) =>
            AvailableSessions.FirstOrDefault(s => side.ConnectionId is { } id && s.Connection.Id == id && s.Server is not null)
            ?? AvailableSessions.FirstOrDefault(s => side.ConnectionId is { } id && s.Connection.Id == id)
            ?? AvailableSessions.FirstOrDefault(s => s.Name == side.ConnectionName);
        if (Find(profile.Source) is not { } source) return $"Open the source connection \"{profile.Source.ConnectionName}\" first.";
        if (Find(profile.Target) is not { } target) return $"Open the target connection \"{profile.Target.ConnectionName}\" first.";
        SourceSession = source;
        if (profile.Source.Database is { } sourceDatabase) await SourceDatabase.ChooseAsync(sourceDatabase);
        TargetSession = target;
        if (profile.Target.Database is { } targetDatabase) await TargetDatabase.ChooseAsync(targetDatabase);
        if ((SourceDatabase.Notice ?? TargetDatabase.Notice) is { } notice) return notice;
        BatchSize = profile.BatchSize;
        Transaction = TransactionChoices.First(c => c.Mode == profile.Transaction);
        ContinueOnError = profile.ContinueOnError;
        Mode = ModeChoices.First(c => c.Mode == profile.Mode);
        IncludeIndexes = profile.IncludeIndexes;
        IncludeForeignKeys = profile.IncludeForeignKeys;
        _tick = profile.Includes;
        _tablesLoadedFor = null;
        if (!await LoadTablesAsync()) return Status;
        foreach (var table in Tables.OfType<DataTransferTableChoice>())
            if (profile.Tables.FirstOrDefault(t => t.Name == table.Identity.Name && (t.Schema ?? "") == (table.Identity.Schema ?? ""))?.Mode is { } mode)
                table.Mode = TableModeChoices.First(c => c.Mode == mode);
        var missing = profile.Tables.Count - Tables.Count(t => t.IsSelected);
        Step = DataTransferStep.Options;
        return missing > 0 ? $"Loaded \"{profile.Name}\"; {missing} of its tables no longer exist in the source." : null;
    }

    private void OnDatabaseChanged()
    {
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(Target));
        OnPropertyChanged(nameof(TargetRefusal));
        OnPropertyChanged(nameof(TargetWarning));
        NextCommand.NotifyCanExecuteChanged();
    }

    public ObservableCollection<DatabaseSession> AvailableSessions { get; }
    public ObservableCollection<ExportTableChoice> Tables { get; } = [];
    public string TablesCaption => "Tables to transfer (what happens to existing target tables is chosen on the next page):";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSourceStep), nameof(IsTargetStep), nameof(IsObjectsStep), nameof(IsOptionsStep), nameof(IsPreviewStep),
        nameof(IsRunStep), nameof(StepCaption))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand), nameof(NextCommand), nameof(StartCommand))]
    private DataTransferStep _step;

    public bool IsSourceStep => Step == DataTransferStep.Source;
    public bool IsTargetStep => Step == DataTransferStep.Target;
    public bool IsObjectsStep => Step == DataTransferStep.Objects;
    public bool IsOptionsStep => Step == DataTransferStep.Options;
    public bool IsPreviewStep => Step == DataTransferStep.Preview;
    public bool IsRunStep => Step == DataTransferStep.Run;
    public string StepCaption => $"Step {(int)Step + 1} of 6 · " + Step switch
    {
        DataTransferStep.Source => "Source",
        DataTransferStep.Target => "Target",
        DataTransferStep.Objects => "Tables",
        DataTransferStep.Options => "Options",
        DataTransferStep.Preview => "Preview",
        _ => "Run"
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetRefusal))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private DatabaseSession? _sourceSession;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetRefusal), nameof(TargetWarning))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private DatabaseSession? _targetSession;

    /// <summary>Why the chosen target cannot take the source's tables; empty when it can or none is chosen.</summary>
    public string TargetRefusal => TargetSession == null ? "" : DataTransferJob.Refusal(Source, Target) ?? "";

    /// <summary>The source and target databases: for a connection that shows every database on its server, the one picked.</summary>
    public DataTransferDatabasePicker SourceDatabase { get; }
    public DataTransferDatabasePicker TargetDatabase { get; }
    public DatabaseSession? Source => SourceDatabase.Session ?? SourceSession;
    public DatabaseSession? Target => TargetDatabase.Session ?? TargetSession;
    private Func<DatabaseObjectIdentity, bool>? _tick;

    /// <summary>
    /// Opens on a source chosen in the explorer: its connection (and, on a server, its database) is picked, and the
    /// tables <paramref name="tick"/> accepts are ticked once listed. The wizard goes on to the target.
    /// </summary>
    public async Task StartFromAsync(DatabaseSession source, Func<DatabaseObjectIdentity, bool> tick)
    {
        var root = AvailableSessions.FirstOrDefault(s => ReferenceEquals(s, source))
                   ?? AvailableSessions.FirstOrDefault(s => s.Server != null && ReferenceEquals(s.Server, source.Server));
        SourceSession = root ?? source;
        if (root != null && !ReferenceEquals(root, source) && source.DatabaseLabel is { } database)
            await SourceDatabase.ChooseAsync(database);
        _tick = tick;
        _tablesLoadedFor = null;
        Step = DataTransferStep.Target;
    }

    [ObservableProperty] private int _batchSize = new DataTransferOptions().BatchSize;

    public static System.Collections.Generic.IReadOnlyList<DataTransferTransactionChoice> TransactionChoices { get; } =
    [
        new(DataTransferTransactionMode.PerTable, "One transaction per table"),
        new(DataTransferTransactionMode.PerBatch, "One transaction per batch")
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinueOnError))]
    private DataTransferTransactionChoice _transaction = TransactionChoices[0];

    public static System.Collections.Generic.IReadOnlyList<DataTransferModeChoice> ModeChoices { get; } =
    [
        new(DataTransferTargetMode.CreateOrAppend, "Create missing tables, append to existing ones"),
        new(DataTransferTargetMode.Create, "Create tables; fail on existing ones"),
        new(DataTransferTargetMode.DropAndRecreate, "Drop and recreate existing tables"),
        new(DataTransferTargetMode.Empty, "Empty existing tables, then insert")
    ];

    /// <summary>A table's own mode: the transfer's, or one of <see cref="ModeChoices"/>.</summary>
    public static System.Collections.Generic.IReadOnlyList<DataTransferModeChoice> TableModeChoices { get; } =
        [new(null, "As for the transfer"), .. ModeChoices];

    [ObservableProperty] private DataTransferModeChoice _mode = ModeChoices[0];

    /// <summary>
    /// Confirms a transfer that drops or empties target tables, showing what it may remove; the target connection's name
    /// is typed to go ahead, as for the explorer's destructive actions. Without it, such a transfer does not start.
    /// </summary>
    public Func<DestructiveActionPreview, Task<bool>>? ConfirmDestructive { get; set; }

    /// <summary>Skipping rejected rows needs batches that commit on their own.</summary>
    public bool CanContinueOnError => Transaction.Mode == DataTransferTransactionMode.PerBatch;

    [ObservableProperty] private bool _continueOnError;

    [ObservableProperty] private bool _includeIndexes = true;
    [ObservableProperty] private bool _includeForeignKeys = true;

    /// <summary>What a failure leaves behind on a target whose DDL commits on its own; empty when it is transactional.</summary>
    public string TargetWarning => Target is { Capabilities.TransactionalDdl: false } target
        ? $"{target.ProviderName} commits CREATE TABLE and other definition changes at once: a table that fails after it was " +
          "created stays on the target, and its rows commit before its remaining definition is added."
        : "";

    /// <summary>Each table's outcome and every skipped row, as text to read or copy once the transfer ends.</summary>
    [ObservableProperty] private string _report = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackCommand), nameof(NextCommand), nameof(StartCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _summary = string.Empty;

    private bool CanGoBack() => !IsBusy && Step != DataTransferStep.Source;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back() => Step -= 1;

    private bool CanGoNext() => !IsBusy && Step switch
    {
        DataTransferStep.Source => Source != null && !SourceDatabase.IsOpening,
        DataTransferStep.Target => Target != null && !TargetDatabase.IsOpening && DataTransferJob.Refusal(Source, Target) == null,
        DataTransferStep.Objects => Tables.Any(t => t.IsSelected),
        DataTransferStep.Options or DataTransferStep.Preview => true,
        _ => false
    };

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextAsync()
    {
        if (Step == DataTransferStep.Target && !await LoadTablesAsync()) return;
        if (Step == DataTransferStep.Options && !await LoadPreviewAsync()) return;
        Summary = string.Empty;
        Step += 1;
    }

    /// <summary>
    /// The script the transfer runs for the tables it creates, every note on what does not carry over, and the tables
    /// that cannot be transferred, read before anything runs.
    /// </summary>
    [ObservableProperty] private string _previewScript = string.Empty;

    private async Task<bool> LoadPreviewAsync()
    {
        IsBusy = true;
        Status = "Planning the transfer…";
        try
        {
            var selected = Tables.Where(t => t.IsSelected).Select(t => t.Identity).ToList();
            PreviewScript = await Task.Run(() => DataTransferJob.PreviewAsync(Source!, Target!, selected, BuildOptions()));
            Status = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status = $"The transfer could not be planned: {DiagnosticRedactor.Redact(ex.Message)}";
            return false;
        }
        finally { IsBusy = false; }
    }

    private DataTransferOptions BuildOptions() => new()
    {
        BatchSize = Math.Max(1, BatchSize),
        Transaction = Transaction.Mode,
        ContinueOnError = CanContinueOnError && ContinueOnError,
        Mode = Mode.Mode ?? DataTransferTargetMode.CreateOrAppend,
        IncludeIndexes = IncludeIndexes,
        IncludeForeignKeys = IncludeForeignKeys,
        TableModes = Tables.Where(t => t.IsSelected).OfType<DataTransferTableChoice>().Where(t => t.Mode.Mode != null)
            .ToDictionary(t => t.Identity, t => t.Mode.Mode!.Value)
    };

    /// <summary>Lists the source's tables once per source; the ones already ticked stay ticked when it is listed again.</summary>
    private async Task<bool> LoadTablesAsync()
    {
        var source = Source!;
        if (_tablesLoadedFor == source && Tables.Count > 0) return true;
        IsBusy = true;
        Status = $"Listing the tables of {source.DisplayName}…";
        try
        {
            var catalog = await DumpCatalog.ResolveAsync(source, DumpSelection.Database);
            foreach (var table in Tables) table.PropertyChanged -= OnTableChoiceChanged;
            Tables.Clear();
            foreach (var table in catalog.Tables.Where(t => t.Kind == SchemaNodeType.Table))
            {
                var choice = new DataTransferTableChoice(ObjectNames.SchemaQualified(table), table, _tick?.Invoke(table) ?? true);
                choice.PropertyChanged += OnTableChoiceChanged;
                Tables.Add(choice);
            }
            _tablesLoadedFor = source;
            Status = Tables.Count == 0 ? $"{source.DisplayName} has no tables to transfer." : string.Empty;
            return Tables.Count > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status = $"The tables of {source.DisplayName} could not be listed: {DiagnosticRedactor.Redact(ex.Message)}";
            return false;
        }
        finally { IsBusy = false; }
    }

    private void OnTableChoiceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExportTableChoice.IsSelected)) NextCommand.NotifyCanExecuteChanged();
    }

    partial void OnSourceSessionChanged(DatabaseSession? value)
    {
        Status = string.Empty;
        _tick = null;
        SourceDatabase.Show(value);
    }

    partial void OnTargetSessionChanged(DatabaseSession? value) => TargetDatabase.Show(value);

    [RelayCommand] private void SelectAll() { foreach (var t in Tables) t.IsSelected = true; }
    [RelayCommand] private void SelectNone() { foreach (var t in Tables) t.IsSelected = false; }

    private bool CanStart() => !IsBusy && Step == DataTransferStep.Run;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var selected = Tables.Where(t => t.IsSelected).ToList();
        if (selected.Count == 0 || Source is not { } source || Target is not { } target) return;
        var options = BuildOptions();
        if (DestructivePreview(target, selected.Select(t => t.Identity).ToList(), options) is { } preview &&
            (ConfirmDestructive == null || !await ConfirmDestructive(preview)))
        {
            Summary = "Not started: dropping or emptying target tables was not confirmed.";
            return;
        }
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        IsBusy = true;
        Summary = string.Empty;
        Report = string.Empty;
        foreach (var table in Tables) table.Result = table.IsSelected ? "Waiting…" : string.Empty;
        // Progress arrives on the UI thread, captured here by Progress<T>.
        var progress = new Progress<DataTransferProgress>(p =>
        {
            selected[p.TableIndex].Result = $"Copying… {p.Rows:N0} rows";
            Status = $"{source.DisplayName} → {target.DisplayName} · {selected[p.TableIndex].Name} · {p.Rows:N0} rows";
        });
        try
        {
            var outcomes = await Task.Run(() => DataTransferJob.RunAsync(source, target, selected.Select(t => t.Identity).ToList(),
                options, progress, cancellation.Token));
            for (var i = 0; i < outcomes.Count; i++) selected[i].Result = Describe(outcomes[i]);
            Summary = Summarize(outcomes);
            Report = FormatReport(source, target, outcomes);
            Status = string.Empty;
        }
        catch (Exception ex)
        {
            Summary = $"The transfer could not run: {DiagnosticRedactor.Redact(ex.Message)}";
        }
        finally
        {
            _cancellation = null;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _cancellation?.Cancel();

    /// <summary>What a transfer whose tables drop or empty their targets asks to confirm; null when none does.</summary>
    public static DestructiveActionPreview? DestructivePreview(DatabaseSession target,
        System.Collections.Generic.IReadOnlyList<DatabaseObjectIdentity> tables, DataTransferOptions options)
    {
        var lines = tables.Where(t => DataTransferOptions.IsDestructive(options.ModeFor(t)))
            .Select(t => (options.ModeFor(t) == DataTransferTargetMode.DropAndRecreate ? "Drop and recreate " : "Empty ") +
                         SqlInsertTarget.ForTable(t, target.Capabilities) + " (if it exists)")
            .ToList();
        if (lines.Count == 0) return null;
        return new DestructiveActionPreview("Drop or empty", "target tables on", target.Name, target.Name,
            target.Connection.EnvironmentTier, string.Join(Environment.NewLine, lines));
    }

    public static string Describe(DataTransferTableOutcome outcome)
    {
        var text = outcome.Status switch
        {
            DataTransferTableStatus.Created => $"Created · {outcome.RowsCopied:N0} rows",
            DataTransferTableStatus.Appended => $"Appended {outcome.RowsCopied:N0} rows",
            DataTransferTableStatus.Recreated => $"Recreated · {outcome.RowsCopied:N0} rows",
            DataTransferTableStatus.Emptied => $"Emptied, then {outcome.RowsCopied:N0} rows inserted",
            DataTransferTableStatus.Canceled => "Canceled",
            _ => "Failed"
        };
        if (outcome.Status is DataTransferTableStatus.Failed or DataTransferTableStatus.Canceled && outcome.RowsCopied > 0)
            text += $" · {outcome.RowsCopied:N0} rows kept";
        if (outcome.RowsSkipped > 0) text += $" · {outcome.RowsSkipped:N0} rows skipped";
        if (Succeeded(outcome)) text += $" · {outcome.Elapsed.TotalSeconds:0.0}s";
        if (outcome.Diagnostic != null) text += ": " + outcome.Diagnostic;
        if (outcome.Notes.Count > 0) text += " (" + string.Join("; ", outcome.Notes) + ")";
        return text;
    }

    private static bool Succeeded(DataTransferTableOutcome outcome) =>
        outcome.Status is not (DataTransferTableStatus.Failed or DataTransferTableStatus.Canceled);

    public static string Summarize(System.Collections.Generic.IReadOnlyList<DataTransferTableOutcome> outcomes)
    {
        var copied = outcomes.Count(Succeeded);
        var rows = outcomes.Sum(o => o.RowsCopied);
        var failed = outcomes.Count(o => o.Status == DataTransferTableStatus.Failed);
        var canceled = outcomes.Count(o => o.Status == DataTransferTableStatus.Canceled);
        var skipped = outcomes.Sum(o => o.RowsSkipped);
        return $"Transferred {copied} of {outcomes.Count} table{(outcomes.Count == 1 ? "" : "s")} · {rows:N0} rows" +
               (skipped > 0 ? $" · {skipped:N0} rows skipped" : "") +
               (failed > 0 ? $" · {failed} failed" : "") + (canceled > 0 ? $" · {canceled} canceled" : "") + ".";
    }

    public static string FormatReport(DatabaseSession source, DatabaseSession target,
        System.Collections.Generic.IReadOnlyList<DataTransferTableOutcome> outcomes)
    {
        var report = new System.Text.StringBuilder();
        report.AppendLine($"Data transfer {source.DisplayName} → {target.DisplayName}");
        report.AppendLine(Summarize(outcomes));
        foreach (var outcome in outcomes)
        {
            report.AppendLine().AppendLine($"{ObjectNames.SchemaQualified(outcome.Table)}: {Describe(outcome)}");
            foreach (var row in outcome.Skipped)
                report.AppendLine($"  skipped row {row.RowNumber}{(row.Key.Length > 0 ? " (" + row.Key + ")" : "")}: {row.Error}");
            if (outcome.RowsSkipped > outcome.Skipped.Count)
                report.AppendLine($"  … and {outcome.RowsSkipped - outcome.Skipped.Count} more skipped rows not listed.");
        }
        return report.ToString();
    }
}

/// <summary>A target mode with the words the wizard shows for it; a null mode means the transfer's own.</summary>
public sealed record DataTransferModeChoice(DataTransferTargetMode? Mode, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A table to transfer, with its own target mode.</summary>
public sealed partial class DataTransferTableChoice(string name, DatabaseObjectIdentity identity, bool isSelected)
    : ExportTableChoice(name, identity, isSelected)
{
    [ObservableProperty] private DataTransferModeChoice _mode = DataTransferViewModel.TableModeChoices[0];
}

/// <summary>A transaction mode with the words the wizard shows for it.</summary>
public sealed record DataTransferTransactionChoice(DataTransferTransactionMode Mode, string Label)
{
    public override string ToString() => Label;
}
