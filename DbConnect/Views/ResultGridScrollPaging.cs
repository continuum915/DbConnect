using System;
using System.Collections;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using DbConnect.ViewModels;

namespace DbConnect.Views;

/// <summary>
/// Asks the tab's <see cref="ResultPagingViewModel"/> for the next page when the results grid's vertical
/// scrollbar reaches its end, and scrolls back to the last row seen before the grid was rebuilt with the
/// added rows.
/// </summary>
internal sealed class ResultGridScrollPaging
{
    private readonly DataGrid _grid;
    private readonly Func<ResultPagingViewModel?> _paging;
    private int? _lastSeenIndex;
    private bool _rebuilding;
    // One fetch per arrival at the end: re-armed only once the scrollbar has moved away from it.
    private bool _armed = true;

    private ResultGridScrollPaging(DataGrid grid, Func<ResultPagingViewModel?> paging)
    {
        _grid = grid;
        _paging = paging;
        grid.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<ScrollBar>("PART_VerticalScrollbar") is { } bar)
                bar.PropertyChanged += (_, change) =>
                {
                    if (change.Property == RangeBase.ValueProperty) OnScrolled(bar);
                };
        };
        grid.PropertyChanged += (_, e) =>
        {
            if (e.Property != ItemsControl.ItemsSourceProperty || _lastSeenIndex is not { } index) return;
            _lastSeenIndex = null;
            // Swapping the rows moves the scrollbar; ignore that until the position is restored.
            _rebuilding = true;
            Dispatcher.UIThread.Post(() =>
            {
                if (_grid.ItemsSource is IList items && index < items.Count) _grid.ScrollIntoView(items[index], null);
                Dispatcher.UIThread.Post(() => _rebuilding = false, DispatcherPriority.Background);
            }, DispatcherPriority.Background);
        };
    }

    public static void Attach(DataGrid grid, Func<ResultPagingViewModel?> paging) => _ = new ResultGridScrollPaging(grid, paging);

    private void OnScrolled(ScrollBar bar)
    {
        var atEnd = bar.Maximum > 0 && bar.Value >= bar.Maximum - bar.SmallChange;
        if (!atEnd)
        {
            if (!_rebuilding) _armed = true;
            return;
        }
        if (!_armed || _rebuilding || _paging() is not { HasMore: true, IsFetching: false } paging) return;
        _armed = false;
        _lastSeenIndex = _grid.ItemsSource?.Cast<object>().Count() - 1;
        paging.OnScrolledToEnd();
    }
}
