using System;
using CommunityToolkit.Mvvm.ComponentModel;
using DbConnect.Models;

namespace DbConnect.ViewModels;

/// <summary>
/// The window's side panes: the sidebar's lower region (the height of the Snippets area under the tree and whether each
/// section is expanded) and the right-hand Information pane (shown, width). Kept in <see cref="AppSettings"/> once
/// attached, so the layout survives a restart.
/// </summary>
public sealed partial class SidebarState : ObservableObject
{
    public const double DefaultLowerHeight = 280;
    public const double MinimumLowerHeight = 80;
    public const double DefaultInformationPaneWidth = 260;
    public const double MinimumInformationPaneWidth = 160;

    private AppSettings? _settings;
    private Action? _save;

    [ObservableProperty]
    private bool _snippetsExpanded = true;

    [ObservableProperty]
    private double _lowerHeight = DefaultLowerHeight;

    [ObservableProperty]
    private bool _informationPaneVisible = true;

    [ObservableProperty]
    private double _informationPaneWidth = DefaultInformationPaneWidth;

    /// <summary>Takes the saved layout from <paramref name="settings"/>; later changes are written back and saved.</summary>
    public void Attach(AppSettings settings, Action save)
    {
        _settings = null;
        SnippetsExpanded = settings.SidebarSnippetsExpanded;
        LowerHeight = settings.SidebarLowerHeight >= MinimumLowerHeight ? settings.SidebarLowerHeight : DefaultLowerHeight;
        InformationPaneVisible = settings.InformationPaneVisible;
        InformationPaneWidth = settings.InformationPaneWidth >= MinimumInformationPaneWidth
            ? settings.InformationPaneWidth : DefaultInformationPaneWidth;
        _settings = settings;
        _save = save;
    }

    partial void OnSnippetsExpandedChanged(bool value) => Store(s => s.SidebarSnippetsExpanded = value);

    partial void OnLowerHeightChanged(double value) => Store(s => s.SidebarLowerHeight = value);

    partial void OnInformationPaneVisibleChanged(bool value) => Store(s => s.InformationPaneVisible = value);

    partial void OnInformationPaneWidthChanged(double value) => Store(s => s.InformationPaneWidth = value);

    private void Store(Action<AppSettings> write)
    {
        if (_settings == null) return;
        write(_settings);
        _save?.Invoke();
    }
}
