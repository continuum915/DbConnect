using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DbConnect.Models;

namespace DbConnect.ViewModels;

/// <summary>
/// Represents a collapsible group of connections in the connection manager left-panel.
/// </summary>
public partial class ConnectionGroup : ObservableObject
{
    [ObservableProperty]
    private string _groupName = string.Empty;

    [ObservableProperty]
    private bool _isExpanded = true;

    public ObservableCollection<ConnectionInfo> Connections { get; } = new();

    [ObservableProperty]
    private ConnectionInfo? _selectedConnection;

    /// <summary>
    /// Fires when the user selects a connection within this group.
    /// The callback should clear selection in all other groups and
    /// update the parent view-model's SelectedSavedConnection.
    /// </summary>
    public Action<ConnectionInfo?>? SelectionChanged { get; set; }

    partial void OnSelectedConnectionChanged(ConnectionInfo? value)
    {
        SelectionChanged?.Invoke(value);
    }
}
