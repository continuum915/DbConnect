namespace DbConnect.ViewModels;

/// <summary>The kind of object the Objects tab lists, chosen from the main toolbar or the navigator.</summary>
public enum ObjectBrowserKind
{
    Tables,
    Views,
    Functions,
    Procedures,
    Triggers,
    Events,
    Sequences,
    MaterializedViews,
    Queries,
}

public static class ObjectBrowserKindNames
{
    /// <summary>The plural label used in menus, the Objects tab and the status bar ("Tables", "Materialized Views").</summary>
    public static string Label(this ObjectBrowserKind kind) => kind switch
    {
        ObjectBrowserKind.MaterializedViews => "Materialized Views",
        _ => kind.ToString(),
    };
}
