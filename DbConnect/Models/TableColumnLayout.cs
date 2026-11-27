using System;
using System.Collections.Generic;
using System.Linq;

namespace DbConnect.Models;

/// <summary>
/// RAIN-P2-14 — A table grid's column layout, persisted per connection and table: the user's column
/// order, the columns hidden from the grid, and how many leading visible columns stay frozen.
/// A saved layout may name columns the table no longer has, and miss ones added since; every
/// method here takes the table's current columns and reconciles against them.
/// </summary>
public sealed class TableColumnLayout
{
    public List<string> Order { get; set; } = [];
    public List<string> Hidden { get; set; } = [];
    public int FrozenCount { get; set; }

    public TableColumnLayout Clone() => new() { Order = [.. Order], Hidden = [.. Hidden], FrozenCount = FrozenCount };

    /// <summary>The current columns in layout order: saved order first, then new columns in source order.</summary>
    public List<string> Arrange(IEnumerable<string> columns)
    {
        var current = columns.ToList();
        var arranged = Order.Where(current.Contains).Distinct(StringComparer.Ordinal).ToList();
        arranged.AddRange(current.Where(c => !arranged.Contains(c)));
        return arranged;
    }

    public List<string> VisibleAmong(IEnumerable<string> columns) =>
        Arrange(columns).Where(c => !Hidden.Contains(c)).ToList();

    public List<string> HiddenAmong(IEnumerable<string> columns) =>
        Arrange(columns).Where(Hidden.Contains).ToList();

    public int FrozenAmong(IEnumerable<string> columns) =>
        Math.Clamp(FrozenCount, 0, VisibleAmong(columns).Count);

    // Hiding, showing and moving keep the same columns frozen: the frozen count follows a column
    // that leaves or enters the frozen leading run, rather than freezing whichever column slides in.

    /// <summary>Hides a visible column; refused when it is the last one visible.</summary>
    public bool Hide(IReadOnlyList<string> columns, string column)
    {
        var visible = VisibleAmong(columns);
        var index = visible.IndexOf(column);
        if (index < 0 || visible.Count <= 1)
            return false;

        var frozen = FrozenAmong(columns);
        Hidden.Add(column);
        FrozenCount = index < frozen ? frozen - 1 : frozen;
        return true;
    }

    public bool Show(IReadOnlyList<string> columns, string column)
    {
        if (!HiddenAmong(columns).Contains(column))
            return false;

        var frozen = FrozenAmong(columns);
        Hidden.RemoveAll(c => c == column);
        FrozenCount = VisibleAmong(columns).IndexOf(column) < frozen ? frozen + 1 : frozen;
        return true;
    }

    public bool ShowAll(IReadOnlyList<string> columns)
    {
        var hidden = HiddenAmong(columns);
        foreach (var column in hidden)
            Show(columns, column);
        Hidden.Clear(); // also drops entries for columns the table no longer has
        return hidden.Count > 0;
    }

    /// <summary>Moves a visible column to <paramref name="visibleIndex"/> among the visible columns; hidden columns keep their relative place.</summary>
    public bool Move(IReadOnlyList<string> columns, string column, int visibleIndex)
    {
        var visible = VisibleAmong(columns);
        var from = visible.IndexOf(column);
        var others = visible.Where(c => c != column).ToList();
        if (from < 0 || others.Count == 0)
            return false;

        var frozen = FrozenAmong(columns);
        var to = Math.Clamp(visibleIndex, 0, others.Count);
        var order = Arrange(columns);
        order.Remove(column);
        order.Insert(to < others.Count ? order.IndexOf(others[to]) : order.IndexOf(others[^1]) + 1, column);
        Order = order;

        if (from < frozen && to >= frozen)
            FrozenCount = frozen - 1;
        else if (from >= frozen && to < frozen)
            FrozenCount = frozen + 1;
        else
            FrozenCount = frozen;
        return true;
    }

    /// <summary>Freezes the visible columns up to and including <paramref name="column"/>.</summary>
    public bool FreezeThrough(IReadOnlyList<string> columns, string column)
    {
        var index = VisibleAmong(columns).IndexOf(column);
        if (index < 0)
            return false;

        FrozenCount = index + 1;
        return true;
    }

    public bool Unfreeze()
    {
        if (FrozenCount == 0)
            return false;

        FrozenCount = 0;
        return true;
    }
}
