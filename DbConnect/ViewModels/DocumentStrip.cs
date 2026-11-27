using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DbConnect.ViewModels;

/// <summary>
/// What the document tab strip shows: the permanent Objects tab first, then the window's document tabs. The window's
/// <c>Tabs</c> stay documents only; this list mirrors each change to them in place (so a document's view is never
/// rebuilt), and its selection maps the Objects tab to "no document selected".
/// </summary>
public sealed partial class DocumentStrip : ObservableObject
{
    private readonly ObservableCollection<ViewModelBase> _documents;
    private readonly Func<ViewModelBase?> _selected;
    private readonly Action<ViewModelBase?> _select;
    private ViewModelBase? _selectedItem;

    public DocumentStrip(ObservableCollection<ViewModelBase> documents, ObjectsTabViewModel objects,
        Func<ViewModelBase?> selected, Action<ViewModelBase?> select)
    {
        _documents = documents;
        _selected = selected;
        _select = select;
        Objects = objects;
        Items.Add(objects);
        foreach (var document in documents) Items.Add(document);
        documents.CollectionChanged += OnDocumentsChanged;
        _selectedItem = selected() ?? objects;
    }

    public ObjectsTabViewModel Objects { get; }

    /// <summary>The strip's tabs: Objects, then the documents in order.</summary>
    public ObservableCollection<ViewModelBase> Items { get; } = [];

    /// <summary>The strip's selected tab: the selected document, or Objects when none is selected.</summary>
    public ViewModelBase? SelectedItem
    {
        get => _selectedItem;
        set
        {
            // The strip may clear its selection while items move; keep showing what the window shows.
            var item = value ?? _selected() ?? Objects;
            if (ReferenceEquals(item, _selectedItem)) return;
            _selectedItem = item;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsObjectsSelected));
            _select(ReferenceEquals(item, Objects) ? null : item);
        }
    }

    public bool IsObjectsSelected => ReferenceEquals(_selectedItem, Objects);

    /// <summary>Follows the window's selected document (call when it changes).</summary>
    public void SyncSelection()
    {
        var item = _selected() ?? Objects;
        if (ReferenceEquals(item, _selectedItem)) return;
        _selectedItem = item;
        OnPropertyChanged(nameof(SelectedItem));
        OnPropertyChanged(nameof(IsObjectsSelected));
    }

    private void OnDocumentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems != null:
                for (var i = 0; i < e.NewItems.Count; i++)
                    Items.Insert(e.NewStartingIndex + 1 + i, (ViewModelBase)e.NewItems[i]!);
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems != null:
                for (var i = 0; i < e.OldItems.Count; i++)
                    Items.RemoveAt(e.OldStartingIndex + 1);
                break;
            case NotifyCollectionChangedAction.Move:
                Items.Move(e.OldStartingIndex + 1, e.NewStartingIndex + 1);
                break;
            case NotifyCollectionChangedAction.Replace when e.NewItems != null:
                for (var i = 0; i < e.NewItems.Count; i++)
                    Items[e.NewStartingIndex + 1 + i] = (ViewModelBase)e.NewItems[i]!;
                break;
            default:
                while (Items.Count > 1) Items.RemoveAt(Items.Count - 1);
                foreach (var document in _documents) Items.Add(document);
                break;
        }

        SyncSelection();
    }
}
