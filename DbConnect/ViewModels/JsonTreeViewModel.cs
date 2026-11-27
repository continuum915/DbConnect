using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>
/// One visible row of the JSON tree: a value, or a range group standing in for a slice of a large
/// array's or object's children. It reads from its <see cref="JsonElement"/> only when shown or expanded.
/// </summary>
public sealed partial class JsonTreeRow : ObservableObject
{
    private readonly JsonTreeViewModel _owner;
    private readonly string? _fullString;

    internal JsonTreeRow(JsonTreeViewModel owner, int depth, string label, JsonElement element, int start, int count, bool isGroup)
    {
        _owner = owner;
        Depth = depth;
        Label = label;
        Element = element;
        Start = start;
        Count = count;
        IsGroup = isGroup;

        if (isGroup)
        {
            Value = string.Empty;
            IsExpandable = true;
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                Value = $"{{{count}}}";
                IsExpandable = count > 0;
                break;
            case JsonValueKind.Array:
                Value = $"[{count}]";
                IsExpandable = count > 0;
                break;
            case JsonValueKind.String:
                var text = element.GetString() ?? string.Empty;
                IsTruncated = text.Length > CellViewerLimits.TreeStringMaxChars;
                _fullString = IsTruncated ? text : null;
                var shown = CellViewerLimits.TreeStringMaxChars;
                if (IsTruncated && char.IsHighSurrogate(text[shown - 1]))
                    shown--;
                Value = IsTruncated ? $"\"{text[..shown]}…\"" : $"\"{text}\"";
                break;
            default:
                Value = element.GetRawText();
                break;
        }
    }

    public int Depth { get; }
    public double Indent => Depth * 16;
    public string Label { get; }
    public string Value { get; }
    public bool IsExpandable { get; }
    public bool IsTruncated { get; }

    internal JsonElement Element { get; }
    /// <summary>The first child this row covers, and how many: all of them unless it's a range group.</summary>
    internal int Start { get; }
    internal int Count { get; }
    internal bool IsGroup { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Glyph))]
    private bool _isExpanded;

    public string Glyph => IsExpandable ? (IsExpanded ? "▾" : "▸") : string.Empty;

    [RelayCommand]
    private void Toggle() => _owner.Toggle(this);

    [RelayCommand]
    private void OpenFullString()
    {
        if (_fullString != null)
            _owner.RequestOpenString(Label, _fullString);
    }
}

/// <summary>
/// RAIN-P2-18 — The cell value viewer's JSON tree as a flat list of rows (each with a depth and an
/// expander) for a virtualizing list. A node's children become rows only when it is expanded, and an
/// array or object with more than <see cref="CellViewerLimits.TreeGroupSize"/> children is split into
/// range groups that are themselves expanded on demand.
/// </summary>
public sealed class JsonTreeViewModel
{
    public JsonTreeViewModel(JsonElement root)
    {
        var rootRow = CreateRow(0, "(root)", root);
        Rows.Add(rootRow);
        Toggle(rootRow);
    }

    public ObservableCollection<JsonTreeRow> Rows { get; } = [];

    /// <summary>A truncated string's row asked for the whole string: its label and text.</summary>
    public event Action<string, string>? OpenStringRequested;

    internal void RequestOpenString(string label, string text) => OpenStringRequested?.Invoke(label, text);

    public void Toggle(JsonTreeRow row)
    {
        if (!row.IsExpandable)
            return;

        var index = Rows.IndexOf(row);
        if (index < 0)
            return;

        if (row.IsExpanded)
        {
            while (index + 1 < Rows.Count && Rows[index + 1].Depth > row.Depth)
                Rows.RemoveAt(index + 1);
        }
        else
        {
            foreach (var child in ChildrenOf(row))
                Rows.Insert(++index, child);
        }

        row.IsExpanded = !row.IsExpanded;
    }

    private IEnumerable<JsonTreeRow> ChildrenOf(JsonTreeRow row)
    {
        var depth = row.Depth + 1;
        if (!row.IsGroup && row.Count > CellViewerLimits.TreeGroupSize)
        {
            for (var start = 0; start < row.Count; start += CellViewerLimits.TreeGroupSize)
            {
                var count = Math.Min(CellViewerLimits.TreeGroupSize, row.Count - start);
                yield return new JsonTreeRow(this, depth, $"[{start}…{start + count - 1}]", row.Element, start, count, isGroup: true);
            }
            yield break;
        }

        if (row.Element.ValueKind == JsonValueKind.Array)
        {
            var i = row.Start;
            foreach (var item in row.Element.EnumerateArray().Skip(row.Start).Take(row.Count))
                yield return CreateRow(depth, $"[{i++}]", item);
        }
        else
        {
            foreach (var property in row.Element.EnumerateObject().Skip(row.Start).Take(row.Count))
                yield return CreateRow(depth, property.Name, property.Value);
        }
    }

    private JsonTreeRow CreateRow(int depth, string label, JsonElement element)
    {
        var count = element.ValueKind switch
        {
            JsonValueKind.Array => element.GetArrayLength(),
            JsonValueKind.Object => element.EnumerateObject().Count(),
            _ => 0,
        };
        return new JsonTreeRow(this, depth, label, element, 0, count, isGroup: false);
    }
}
