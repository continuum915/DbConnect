using System;
using CommunityToolkit.Mvvm.ComponentModel;
using DbConnect.Models;

namespace DbConnect.ViewModels;

/// <summary>
/// A restored tab remembers the connection it was on until that connection opens. Connections are not open
/// at startup, so without this a restored tab lost its connection and later took whichever one opened first.
/// </summary>
public partial class QueryTabViewModel
{
    /// <summary>The saved connection this tab waits for while no connection is selected; null when none was saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionPlaceholder))]
    private RestoredConnection? _waitingForConnection;

    /// <summary>The connection to save with the tab: the selected one, or the one it is still waiting for.</summary>
    public Guid? ConnectionIdToSave => SelectedSession?.Connection.Id ?? WaitingForConnection?.Id;

    /// <inheritdoc cref="ConnectionIdToSave"/>
    public string? ConnectionNameToSave => SelectedSession?.Connection.Name ?? WaitingForConnection?.Name;

    /// <summary>What the connection picker shows while no connection is selected.</summary>
    public string ConnectionPlaceholder => WaitingForConnection?.Name is { } name
        ? $"{name} (connect in the explorer)"
        : "No connection";
}

/// <summary>A saved connection reference: by id, or by name for sessions saved before ids were recorded.</summary>
public sealed record RestoredConnection(Guid? Id, string? Name)
{
    /// <summary>Null when nothing was saved.</summary>
    public static RestoredConnection? From(Guid? id, string? name) =>
        id == null && string.IsNullOrEmpty(name) ? null : new RestoredConnection(id, name);

    public bool Matches(ConnectionInfo connection) => Id is { } id ? id == connection.Id : Name == connection.Name;
}
