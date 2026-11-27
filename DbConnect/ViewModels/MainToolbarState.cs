using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DbConnect.ViewModels;

/// <summary>
/// The main toolbar's own state: which object type the Objects tab shows. The Table, View and Function tiles and the
/// Others menu switch it; the tile for the current type is drawn checked. The toolbar's other tiles run existing
/// window commands.
/// </summary>
public sealed partial class MainToolbarState : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTablesShown), nameof(IsViewsShown), nameof(IsFunctionsShown), nameof(IsOtherShown),
        nameof(IsQueriesShown))]
    private ObjectBrowserKind _objectType = ObjectBrowserKind.Tables;

    public bool IsTablesShown => ObjectType == ObjectBrowserKind.Tables;
    public bool IsViewsShown => ObjectType == ObjectBrowserKind.Views;
    public bool IsFunctionsShown => ObjectType == ObjectBrowserKind.Functions;
    public bool IsQueriesShown => ObjectType == ObjectBrowserKind.Queries;
    public bool IsOtherShown => !IsTablesShown && !IsViewsShown && !IsFunctionsShown && !IsQueriesShown;

    /// <summary>The object types under the Others tile.</summary>
    public IReadOnlyList<ObjectBrowserKind> OtherKinds { get; } =
    [
        ObjectBrowserKind.Procedures,
        ObjectBrowserKind.Triggers,
        ObjectBrowserKind.Events,
        ObjectBrowserKind.Sequences,
        ObjectBrowserKind.MaterializedViews,
    ];

    [RelayCommand]
    private void ShowObjects(ObjectBrowserKind kind) => ObjectType = kind;
}
