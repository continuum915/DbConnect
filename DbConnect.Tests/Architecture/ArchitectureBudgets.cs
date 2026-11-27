using System.Collections.Generic;

namespace DbConnect.Tests.Architecture;

/// <summary>
/// Size budgets checked by <see cref="ArchitectureFitnessTests"/>. Entries are the files and types that were
/// already past the default caps when the ratchet was introduced (2026-09-30), pinned at their size then.
/// Lower an entry when its class shrinks; do not raise one to fit new code. Remove it once it's back under
/// the default.
/// </summary>
public static class ArchitectureBudgets
{
    public const int DefaultFileLines = 600;
    /// <summary>How far a file may shrink below its budget before the budget must be lowered.</summary>
    public const int FileLinesSlack = 25;

    public const int DefaultTypeMethods = 40;
    public const int TypeMethodsSlack = 2;

    /// <summary>Methods and properties on <c>IDatabaseProvider</c>; new provider behaviour goes on a smaller interface.</summary>
    public const int ProviderInterfaceMembers = 8;

    public static IReadOnlyDictionary<string, int> FileLines { get; } = new Dictionary<string, int>
    {
        ["DbConnect/Services/DataCompareService.cs"] = 650,
        ["DbConnect/Services/DependencyAnalysisService.cs"] = 618,
        ["DbConnect/Services/SchemaDiffService.cs"] = 621,
        ["DbConnect/ViewModels/ErDiagramViewModel.cs"] = 928,
        ["DbConnect/ViewModels/MainWindowViewModel.cs"] = 1047,
        ["DbConnect/ViewModels/QueryTabViewModel.cs"] = 726,
        ["DbConnect/ViewModels/TableDataTabViewModel.cs"] = 968,
        ["DbConnect/Views/MainWindow.axaml.cs"] = 637,
        ["DbConnect/Views/TableDataTabView.axaml.cs"] = 650,
    };

    public static IReadOnlyDictionary<string, int> TypeMethods { get; } = new Dictionary<string, int>
    {
        ["DbConnect.ViewModels.ErDiagramViewModel"] = 49,
        ["DbConnect.ViewModels.MainWindowViewModel"] = 71,
        ["DbConnect.ViewModels.TableDataTabViewModel"] = 69,
        ["DbConnect.Views.MainWindow"] = 53,
        ["DbConnect.Views.TableDataTabView"] = 45,
    };

    /// <summary>Quoted provider names ("PostgreSQL", "SQL Server", ...) in view models and views; each should become a capability flag.</summary>
    public static IReadOnlyDictionary<string, int> ProviderNameLiterals { get; } = new Dictionary<string, int>();
}
