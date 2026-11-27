using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>One step of the last save, as the outcome list shows it.</summary>
public sealed record TableChangeStepRow(int Number, string Status, string Sql, string? Error);

/// <summary>
/// The designer's review of a plan before Save (destructive changes to confirm, the commit
/// boundary) and of the last save afterwards (each step's outcome, and whether the table must be
/// reloaded before saving again). Confirmation belongs to one plan: a different plan clears it.
/// </summary>
public sealed partial class TableChangeReview : ObservableObject
{
    private string? _confirmedSql;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsConfirmation))]
    private bool _destructiveConfirmed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDestructive), nameof(NeedsConfirmation))]
    private TableChangePlan _plan = TableChangePlan.None;

    /// <summary>Shown before Save when the steps commit one at a time.</summary>
    [ObservableProperty] private string? _commitBoundary;

    /// <summary>Why Save must wait for a reload, after a stale baseline or a save that failed part-way.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiresReload))]
    private string? _reloadReason;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutcome))]
    private TableChangeOutcome? _outcome;

    public ObservableCollection<string> Destructive { get; } = new();
    public ObservableCollection<TableChangeStepRow> Steps { get; } = new();

    public bool HasDestructive => Plan.Destructive.Count > 0;
    public bool NeedsConfirmation => HasDestructive && !DestructiveConfirmed;
    public bool RequiresReload => ReloadReason != null;
    public bool HasOutcome => Outcome != null;

    /// <summary>Shows a new plan; a confirmation given for different SQL no longer applies.</summary>
    public void Show(TableChangePlan plan, bool transactionalDdl)
    {
        Plan = plan;
        if (_confirmedSql != plan.Sql) DestructiveConfirmed = false;
        Destructive.Clear();
        foreach (var item in plan.Destructive) Destructive.Add(item);
        CommitBoundary = !transactionalDdl && plan.Steps.Count > 1
            ? $"This engine commits each schema change as it runs. The {plan.Steps.Count} steps run in order; if one fails, the steps before it stay applied."
            : null;
    }

    partial void OnDestructiveConfirmedChanged(bool value) => _confirmedSql = value ? Plan.Sql : null;

    public void ShowOutcome(TableChangeOutcome? outcome)
    {
        Outcome = outcome;
        Steps.Clear();
        if (outcome == null) return;
        foreach (var (step, i) in outcome.Steps.Select((s, i) => (s, i)))
            Steps.Add(new(i + 1, Words(step.Status), step.Sql, step.Error));
    }

    private static string Words(TableChangeStepStatus status) => status switch
    {
        TableChangeStepStatus.Applied => "Applied",
        TableChangeStepStatus.Failed => "Failed",
        TableChangeStepStatus.RolledBack => "Rolled back",
        TableChangeStepStatus.Uncertain => "Unknown",
        _ => "Not run"
    };
}
