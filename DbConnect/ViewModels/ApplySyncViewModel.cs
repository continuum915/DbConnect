using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>What a comparison's Preview builds: the target to apply to and the plan, or why nothing can be applied.</summary>
public sealed record SyncPreview(DatabaseSession? Target, SyncPlan? Plan, string? Refusal = null);

/// <summary>
/// Preview → Apply for Schema Diff and Data Compare, as Navicat's synchronization does it: Preview shows the exact
/// script with destructive steps marked; Apply asks the destructive-action confirmation naming the target, its
/// database and environment tier (and, with destructive steps, the data-loss tick), runs the plan on a session of its
/// own (<see cref="SyncPlanExecutor"/>), shows each step's outcome, and re-compares when every step applied. The log
/// can be saved as SQL with the results as comments, or as text.
/// </summary>
public sealed partial class ApplySyncViewModel(Func<Task<SyncPreview>> preview, Func<Task>? recompare = null) : ObservableObject
{
    private DatabaseSession? _target;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDestructive), nameof(CanApplyPlan), nameof(CanContinueOnError))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private SyncPlan? _plan;

    [ObservableProperty] private string _script = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyPlan))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _acknowledgeDataLoss;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand), nameof(ApplyCommand), nameof(RecompareCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _status = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveLogCommand))]
    private SyncOutcome? _outcome;

    [ObservableProperty] private string _log = string.Empty;

    public ObservableCollection<string> DestructiveSteps { get; } = [];

    /// <summary>Steps commit as they run (MySQL definitions), so going on after a failure is offered; elsewhere a failure undoes everything.</summary>
    public bool CanContinueOnError => Plan?.Steps.Any(s => s.CommitsImplicitly) == true;

    [ObservableProperty] private bool _continueOnError;
    public bool HasDestructive => Plan?.DestructiveSteps.Any() == true;
    public bool CanApplyPlan => Plan is { Steps.Count: > 0 } && (!HasDestructive || AcknowledgeDataLoss);

    /// <summary>The destructive-action confirmation; without it nothing is applied.</summary>
    public Func<DestructiveActionPreview, Task<bool>>? Confirm { get; set; }

    /// <summary>Asks where to save the log; the extension (.sql or .txt) picks the form. Null when canceled.</summary>
    public Func<string, Task<string?>>? PickLogPath { get; set; }

    /// <summary>The script opens in a query tab for review or a manual run.</summary>
    public Action<string>? OpenInTab { get; set; }

    /// <summary>The differences changed: the plan no longer matches them and must be previewed again.</summary>
    public void Invalidate()
    {
        Plan = null;
        Script = string.Empty;
        DestructiveSteps.Clear();
        AcknowledgeDataLoss = false;
    }

    private bool CanPreview() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanPreview))]
    private async Task PreviewAsync()
    {
        IsBusy = true;
        Invalidate();
        Outcome = null;
        Log = string.Empty;
        try
        {
            var built = await preview();
            if (built.Refusal != null || built.Plan == null || built.Target == null)
            {
                Status = built.Refusal ?? "There is nothing to apply.";
                return;
            }
            _target = built.Target;
            Plan = built.Plan;
            foreach (var step in built.Plan.DestructiveSteps) DestructiveSteps.Add($"{step.Object}: {step.Destructive}");
            var rules = built.Target.Capabilities.ScriptRules;
            Script = built.Plan.Script(sql => rules.Terminate(sql, compound: false, newLine: "\n"));
            Status = built.Plan.Steps.Count == 0 ? "There is nothing to apply."
                : $"{built.Plan.Steps.Count} step{(built.Plan.Steps.Count == 1 ? "" : "s")} to apply" +
                  (HasDestructive ? $", {DestructiveSteps.Count} destructive." : ".");
        }
        catch (Exception ex)
        {
            Status = $"The changes could not be planned: {DiagnosticRedactor.Redact(ex.Message)}";
        }
        finally { IsBusy = false; }
    }

    private bool CanApply() => !IsBusy && CanApplyPlan;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (Plan is not { } plan || _target is not { } target) return;
        var where = plan.TargetDatabase is { } database ? $"{target.Name} / {database}" : target.Name;
        var verb = CanContinueOnError ? "Apply (cannot be rolled back)" : "Apply";
        var confirmation = new DestructiveActionPreview(verb, $"{plan.Steps.Count} change{(plan.Steps.Count == 1 ? "" : "s")} to",
            target.Name, where, plan.EnvironmentTier, Script);
        if (Confirm == null || !await Confirm(confirmation))
        {
            Status = "Not applied: the changes were not confirmed.";
            return;
        }
        IsBusy = true;
        Status = $"Applying {plan.Steps.Count} steps to {where}…";
        try
        {
            var goOn = CanContinueOnError && ContinueOnError;
            Outcome = await Task.Run(() => SyncPlanExecutor.ExecuteAsync(target, plan, continueOnError: goOn));
            Log = Outcome.Log(asSql: false);
            Status = Outcome.Summary;
        }
        finally { IsBusy = false; }
        if (Outcome.Kind == SyncOutcomeKind.Applied && recompare != null)
        {
            Plan = null;
            await recompare();
            Status = Outcome.Summary + " Compared again.";
        }
    }

    private bool CanRecompare() => !IsBusy && recompare != null;

    [RelayCommand(CanExecute = nameof(CanRecompare))]
    private async Task RecompareAsync()
    {
        Invalidate();
        await recompare!();
    }

    private bool HasOutcome() => Outcome != null;

    [RelayCommand(CanExecute = nameof(HasOutcome))]
    private async Task SaveLogAsync()
    {
        if (Outcome == null || PickLogPath == null || await PickLogPath("sync-log.sql") is not { } path) return;
        try
        {
            await File.WriteAllTextAsync(path, Outcome.Log(asSql: !path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)));
            Status = $"Saved the log to {Path.GetFileName(path)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"The log could not be saved: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenScriptInTab()
    {
        if (!string.IsNullOrWhiteSpace(Script)) OpenInTab?.Invoke(Script);
    }
}
