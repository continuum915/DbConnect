using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>Recently opened or saved SQL files, most recent first, without duplicates.</summary>
public sealed class RecentFilesList
{
    public const int Capacity = 10;

    public ObservableCollection<string> Items { get; } = new();

    /// <summary>Called with the new list after every change, to persist it.</summary>
    public Action<IReadOnlyList<string>>? Persist { get; set; }

    /// <summary>Replaces the list with saved paths, without persisting.</summary>
    public void Load(IEnumerable<string> paths)
    {
        Items.Clear();
        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            if (Items.Count == Capacity) break;
            if (!Items.Any(existing => SqlFileService.SamePath(existing, path))) Items.Add(path);
        }
    }

    public void Add(string path)
    {
        var existing = Items.FirstOrDefault(p => SqlFileService.SamePath(p, path));
        if (existing != null) Items.Remove(existing);
        Items.Insert(0, path);
        while (Items.Count > Capacity) Items.RemoveAt(Items.Count - 1);
        Persist?.Invoke(Items.ToList());
    }

    public void Remove(string path)
    {
        var existing = Items.FirstOrDefault(p => SqlFileService.SamePath(p, path));
        if (existing == null) return;
        Items.Remove(existing);
        Persist?.Invoke(Items.ToList());
    }

    public void Clear()
    {
        Items.Clear();
        Persist?.Invoke([]);
    }
}
