using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class SchemaDiffViewModel : ViewModelBase
{
    private readonly SchemaDiffService _svc = new();
    private readonly DdlTranslationService _translationService = new();
    private readonly DependencyAnalysisService _dependencyAnalysisService = new();
    private readonly List<string> _baseScriptWarnings = new();
    private string _baseMigrationScript = string.Empty;

    [ObservableProperty] private DatabaseSession? _sourceSession;
    [ObservableProperty] private DatabaseSession? _targetSession;
    [ObservableProperty] private string _migrationScript = string.Empty;
    [ObservableProperty] private bool _isComparing;
    [ObservableProperty] private string _compareStatus = string.Empty;
    [ObservableProperty] private bool _enableCrossProviderTranslation;
    [ObservableProperty] private ObservableCollection<string> _translationWarnings = new();
    [ObservableProperty] private string _compatibilityReport = string.Empty;
    [ObservableProperty] private string _impactAnalysis = string.Empty;

    public ObservableCollection<string> ScriptWarnings { get; } = new();
    public bool HasScriptWarnings => ScriptWarnings.Count > 0;
    public bool HasImpactAnalysis => !string.IsNullOrWhiteSpace(ImpactAnalysis);

    public ObservableCollection<DatabaseSession> AvailableSessions { get; }
    public ObservableCollection<SchemaDiffItem> DiffItems { get; } = new();

    /// <summary>Wire this to open a script in a new query tab.</summary>
    public Action<string>? RequestOpenInTab { get; set; }

    public SchemaDiffViewModel(ObservableCollection<DatabaseSession> availableSessions)
    {
        Title = "Schema Diff";
        AvailableSessions = availableSessions;

        ScriptWarnings.CollectionChanged += OnWarningsChanged;
        TranslationWarnings.CollectionChanged += OnWarningsChanged;
        Apply = new ApplySyncViewModel(PreviewSyncAsync, () => RunCompareCommand.ExecuteAsync(null))
        {
            OpenInTab = script => RequestOpenInTab?.Invoke(script)
        };
        DiffItems.CollectionChanged += (_, e) =>
        {
            foreach (var item in e.NewItems?.OfType<SchemaDiffItem>() ?? []) item.PropertyChanged += OnItemTicked;
            Apply.Invalidate();
        };
    }

    /// <summary>Preview → Apply of the ticked differences, between connections of one engine.</summary>
    public ApplySyncViewModel Apply { get; }

    private void OnItemTicked(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SchemaDiffItem.IsSelected)) Apply.Invalidate();
    }

    private async Task<SyncPreview> PreviewSyncAsync()
    {
        if (SourceSession is not { } source || TargetSession is not { } target) return new(null, null, "Compare two connections first.");
        if (SchemaSyncPlanner.Refusal(source, target) is { } refusal) return new(null, null, refusal);
        var ticked = DiffItems.Where(i => i.IsSelected && i.ChangeType != SchemaDiffChangeType.None).ToList();
        if (ticked.Count == 0) return new(null, null, "Tick the differences to apply.");
        try
        {
            return new(target, await SchemaSyncPlanner.BuildAsync(source, target, ticked,
                (from, to, created) => TableSyncEditor.PlanAsync(source, from, target, to, created)));
        }
        catch (SchemaDriftException drift)
        {
            return new(null, null, drift.Message);
        }
    }

    // Designer constructor
    public SchemaDiffViewModel() : this(new ObservableCollection<DatabaseSession>()) { }

    partial void OnEnableCrossProviderTranslationChanged(bool value) => ApplyTranslationIfNeeded();

    partial void OnImpactAnalysisChanged(string value) => OnPropertyChanged(nameof(HasImpactAnalysis));

    private void OnWarningsChanged(object? sender, NotifyCollectionChangedEventArgs e) => OnPropertyChanged(nameof(HasScriptWarnings));

    private void ApplyTranslationIfNeeded()
    {
        TranslationWarnings.Clear();
        CompatibilityReport = string.Empty;

        if (SourceSession == null || TargetSession == null || string.IsNullOrWhiteSpace(_baseMigrationScript))
        {
            MigrationScript = _baseMigrationScript;
            RefreshScriptWarnings();
            return;
        }

        var currentScript = _baseMigrationScript;
        if (!string.Equals(SourceSession.ProviderName, TargetSession.ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            TranslationWarnings.Add($"DDL translation warning: source provider '{SourceSession.ProviderName}' differs from target provider '{TargetSession.ProviderName}'. Review translated DDL before execution.");

            if (EnableCrossProviderTranslation)
            {
                var (translated, warnings) = _translationService.TranslateDdl(
                    currentScript,
                    SourceSession.ProviderName,
                    TargetSession.ProviderName);

                currentScript = translated;
                foreach (var warning in warnings.Distinct(StringComparer.OrdinalIgnoreCase))
                    TranslationWarnings.Add(warning);
            }

            var report = _translationService.GetCompatibilityReport(currentScript, TargetSession.ProviderName);
            CompatibilityReport = string.Join("\n", report);
        }

        MigrationScript = currentScript;
        RefreshScriptWarnings();
    }

    [RelayCommand]
    private async Task RunCompare()
    {
        if (SourceSession == null || TargetSession == null)
        {
            CompareStatus = "Select both source and target sessions.";
            return;
        }

        IsComparing = true;
        CompareStatus = "Comparing...";
        DiffItems.Clear();
        ImpactAnalysis = string.Empty;
        _baseMigrationScript = string.Empty;
        MigrationScript = string.Empty;
        _baseScriptWarnings.Clear();
        TranslationWarnings.Clear();
        ScriptWarnings.Clear();

        try
        {
            var result = await _svc.CompareAsync(SourceSession, TargetSession);
            foreach (var item in result.Items)
                DiffItems.Add(item);

            CompareStatus = $"Done. {result.Items.Count} object(s) compared.";
        }
        catch (Exception ex)
        {
            CompareStatus = $"Error: {ex.Message}";
        }
        finally
        {
            IsComparing = false;
        }
    }

    public void LoadPrecomputedDiff(IEnumerable<SchemaDiffItem> items, string migrationScript, string compareStatus)
    {
        DiffItems.Clear();
        foreach (var item in items)
            DiffItems.Add(item);

        CompareStatus = compareStatus;
        ImpactAnalysis = string.Empty;
        _baseScriptWarnings.Clear();
        TranslationWarnings.Clear();
        ScriptWarnings.Clear();
        _baseMigrationScript = migrationScript;
        MigrationScript = migrationScript;
    }

    [RelayCommand]
    private async Task GenerateScriptAsync()
    {
        if (SourceSession == null || TargetSession == null)
        {
            var selectedPrecomputedItems = DiffItems
                .Where(i => i.IsSelected && !string.IsNullOrWhiteSpace(i.GeneratedScript))
                .ToList();

            if (selectedPrecomputedItems.Count == 0)
                return;

            _baseMigrationScript = string.Join(
                Environment.NewLine + Environment.NewLine,
                selectedPrecomputedItems.Select(item => item.GeneratedScript.TrimEnd()));
            ApplyTranslationIfNeeded();
            return;
        }

        _baseScriptWarnings.Clear();
        ImpactAnalysis = string.Empty;

        var selectedItems = DiffItems
            .Where(i => i.IsSelected)
            .ToList();

        foreach (var item in selectedItems)
        {
            item.ChangeType = ResolveChangeType(item);
            item.DependsOn = await BuildDependenciesAsync(item);
            item.GeneratedScript = await BuildItemScriptAsync(item, TargetSession.ProviderName);
        }

        var sortedResult = _dependencyAnalysisService.Sort(selectedItems);
        ImpactAnalysis = sortedResult.ImpactSummary.FormatMessage();

        var sb = new StringBuilder();
        sb.AppendLine($"-- Migration script: {SourceSession.Name} → {TargetSession.Name}");
        sb.AppendLine($"-- Generated: {DateTime.UtcNow:u}");
        sb.AppendLine();

        AppendCircularDependencyComments(sb, sortedResult.CircularGroups);

        foreach (var item in sortedResult.SortedItems.Where(i => !string.IsNullOrWhiteSpace(i.GeneratedScript)))
        {
            sb.AppendLine(item.GeneratedScript.TrimEnd());
            sb.AppendLine();
        }

        _baseMigrationScript = sb.ToString();
        ApplyTranslationIfNeeded();
    }

    private async Task<List<string>> BuildDependenciesAsync(SchemaDiffItem item)
    {
        var session = item.ChangeType == SchemaDiffChangeType.Remove ? TargetSession : SourceSession;
        if (session == null)
            return new List<string>();

        var dependsOn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var metadataDeps = await _dependencyAnalysisService.GetDependsOnAsync(
                session,
                item.ObjectName,
                item.ObjectType);

            foreach (var dep in metadataDeps)
            {
                if (!string.IsNullOrWhiteSpace(dep.DependsOnName))
                    dependsOn.Add(dep.DependsOnName);
            }
        }
        catch
        {
            // Dependency ordering is best-effort.
        }

        var ddl = item.ChangeType == SchemaDiffChangeType.Remove ? item.TargetDdl : item.SourceDdl;
        if (!string.IsNullOrWhiteSpace(ddl))
        {
            foreach (var dep in DependencyAnalysisService.ExtractReferencedObjects(ddl))
                dependsOn.Add(dep);
        }

        return dependsOn.ToList();
    }

    private async Task<string> BuildItemScriptAsync(SchemaDiffItem item, string targetProvider)
    {
        if (item.ChangeType == SchemaDiffChangeType.None)
            return string.Empty;

        try
        {
            return item.ChangeType switch
            {
                SchemaDiffChangeType.Add => await BuildCreateScriptAsync(item, targetProvider),
                SchemaDiffChangeType.Remove => BuildDropScript(item),
                SchemaDiffChangeType.Modify => await BuildModifyScriptAsync(item, targetProvider),
                _ => string.Empty
            };
        }
        catch (Exception ex)
        {
            return BuildUnsupportedScript(item, ex.Message);
        }
    }

    private async Task<string> BuildCreateScriptAsync(SchemaDiffItem item, string targetProvider)
    {
        switch (item.ObjectKind)
        {
            case SchemaDiffObjectType.Table:
                {
                    var cols = await SourceSession!.GetColumnDefinitionsAsync(item.ObjectName);
                    return DdlGenerator.GenerateCreateTable(item.ObjectName, cols, targetProvider);
                }
            case SchemaDiffObjectType.View:
                {
                    var viewDef = await SourceSession!.GetViewDefinitionAsync(GetLookupName(item));
                    if (!string.IsNullOrWhiteSpace(viewDef))
                        return BuildViewScript(item, viewDef, targetProvider);
                    return BuildUnsupportedScript(item, "view definition not available from source");
                }
            case SchemaDiffObjectType.Function:
            case SchemaDiffObjectType.Procedure:
            case SchemaDiffObjectType.Trigger:
                {
                    var definition = await GetRoutineDefinitionAsync(item, SourceSession!);
                    if (!string.IsNullOrWhiteSpace(definition))
                        return EnsureSqlTerminated(definition);
                    return BuildUnsupportedScript(item, "object definition not available from source");
                }
            default:
                return BuildUnsupportedScript(item);
        }
    }

    private async Task<string> BuildModifyScriptAsync(SchemaDiffItem item, string targetProvider)
    {
        switch (item.ObjectKind)
        {
            case SchemaDiffObjectType.View:
                {
                    var viewDef = await SourceSession!.GetViewDefinitionAsync(GetLookupName(item));
                    if (string.IsNullOrWhiteSpace(viewDef))
                        return BuildUnsupportedScript(item, "view definition not available from source");

                    var createScript = BuildViewScript(item, viewDef, targetProvider);
                    return RequiresExplicitDrop(createScript)
                        ? $"{BuildDropScript(item)}{Environment.NewLine}{createScript}"
                        : createScript;
                }
            case SchemaDiffObjectType.Function:
            case SchemaDiffObjectType.Procedure:
            case SchemaDiffObjectType.Trigger:
                {
                    var definition = await GetRoutineDefinitionAsync(item, SourceSession!);
                    if (string.IsNullOrWhiteSpace(definition))
                        return BuildUnsupportedScript(item, "object definition not available from source");

                    var createScript = EnsureSqlTerminated(definition);
                    return RequiresExplicitDrop(createScript)
                        ? $"{BuildDropScript(item)}{Environment.NewLine}{createScript}"
                        : createScript;
                }
            case SchemaDiffObjectType.Table:
                return $"-- TABLE CHANGE: {item.QualifiedName}{Environment.NewLine}-- {item.SourceDdl}";
            default:
                return BuildUnsupportedScript(item);
        }
    }

    private static bool RequiresExplicitDrop(string ddl) =>
        !Regex.IsMatch(ddl, @"\b(?:CREATE\s+OR\s+REPLACE|ALTER)\b", RegexOptions.IgnoreCase);

    private string BuildDropScript(SchemaDiffItem item) =>
        $"DROP {item.ObjectKind.ToSqlKeyword()} IF EXISTS {item.QualifiedName};";

    private async Task<string?> GetRoutineDefinitionAsync(SchemaDiffItem item, DatabaseSession session) => item.ObjectKind switch
    {
        SchemaDiffObjectType.Function => await session.GetFunctionDefinitionAsync(item.SchemaName ?? string.Empty, item.ObjectName),
        SchemaDiffObjectType.Procedure => await session.GetProcedureDefinitionAsync(item.SchemaName ?? string.Empty, item.ObjectName),
        SchemaDiffObjectType.Trigger => await session.GetTriggerDefinitionAsync(item.SchemaName ?? string.Empty, item.ObjectName),
        _ => null
    };

    private string BuildViewScript(SchemaDiffItem item, string viewDef, string targetProvider)
    {
        if (Regex.IsMatch(viewDef, @"\bCREATE\b", RegexOptions.IgnoreCase))
            return EnsureSqlTerminated(viewDef);

        return EnsureSqlTerminated(DdlGenerator.GenerateCreateView(item.ObjectName, viewDef, targetProvider));
    }

    private string BuildUnsupportedScript(SchemaDiffItem item, string? reason = null)
    {
        var detail = reason != null ? $": {reason}" : ". Add manually.";
        _baseScriptWarnings.Add($"UNSUPPORTED: Cannot auto-generate DDL for {item.ObjectType} '{item.QualifiedName}'{detail}");
        RefreshScriptWarnings();
        return $"-- UNSUPPORTED: Create {item.ObjectType} {item.QualifiedName} manually";
    }

    private void RefreshScriptWarnings()
    {
        ScriptWarnings.Clear();
        foreach (var warning in _baseScriptWarnings.Distinct(StringComparer.OrdinalIgnoreCase))
            ScriptWarnings.Add(warning);
        foreach (var warning in TranslationWarnings.Distinct(StringComparer.OrdinalIgnoreCase))
            if (!_baseScriptWarnings.Contains(warning, StringComparer.OrdinalIgnoreCase))
                ScriptWarnings.Add(warning);
    }

    private static SchemaDiffChangeType ResolveChangeType(SchemaDiffItem item) => item.ChangeType != SchemaDiffChangeType.None
        ? item.ChangeType
        : item.Status switch
        {
            DiffStatus.OnlyInSource => SchemaDiffChangeType.Add,
            DiffStatus.OnlyInTarget => SchemaDiffChangeType.Remove,
            DiffStatus.InBoth when !string.IsNullOrWhiteSpace(item.SourceDdl) || !string.IsNullOrWhiteSpace(item.TargetDdl) => SchemaDiffChangeType.Modify,
            _ => SchemaDiffChangeType.None
        };

    private static string GetLookupName(SchemaDiffItem item) => string.IsNullOrWhiteSpace(item.SchemaName)
        ? item.ObjectName
        : item.QualifiedName;

    private static void AppendCircularDependencyComments(StringBuilder sb, IEnumerable<List<string>> circularGroups)
    {
        foreach (var group in circularGroups.Where(g => g.Count > 0))
        {
            sb.AppendLine("-- CIRCULAR DEPENDENCY DETECTED");
            sb.AppendLine($"-- Objects: {string.Join(", ", group)}");
            sb.AppendLine("-- Manual resolution required: review object order and split this group into separate deployment steps.");
            sb.AppendLine();
        }
    }

    private static string EnsureSqlTerminated(string ddl) => ddl.TrimEnd().EndsWith(';') ? ddl.TrimEnd() : $"{ddl.TrimEnd()};";

    [RelayCommand]
    private void OpenScriptInNewTab()
    {
        if (!string.IsNullOrWhiteSpace(MigrationScript))
            RequestOpenInTab?.Invoke(MigrationScript);
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in DiffItems)
            item.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var item in DiffItems)
            item.IsSelected = false;
    }
}
