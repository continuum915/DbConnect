using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>
/// The database picker beside a Data Transfer connection: shown for a connection that lists every database on its
/// server (§1.2), where picking one opens that database's own session for the transfer. A connection pinned to one
/// database shows none, and the connection's own session is used.
/// </summary>
public sealed partial class DataTransferDatabasePicker(Action changed) : ObservableObject
{
    private DatabaseSession? _connection;
    private bool _syncing;

    public ObservableCollection<string> Databases { get; } = [];
    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private string? _selected;
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private bool _isOpening;

    /// <summary>The picked database's session; null when the connection's own session is used.</summary>
    public DatabaseSession? Session { get; private set; }

    /// <summary>Lists the databases of <paramref name="connection"/>'s server, with its own database picked.</summary>
    public void Show(DatabaseSession? connection)
    {
        _syncing = true;
        try
        {
            _connection = connection;
            Session = null;
            Notice = null;
            Databases.Clear();
            foreach (var database in connection?.Server?.Databases ?? []) Databases.Add(database);
            IsAvailable = connection?.Server != null;
            Selected = connection?.DatabaseLabel;
        }
        finally { _syncing = false; }
        changed();
    }

    partial void OnSelectedChanged(string? value)
    {
        if (!_syncing && value != null) _ = ChooseAsync(value);
    }

    /// <summary>Opens <paramref name="database"/> on the connection's server; its own database uses the connection's session.</summary>
    public async Task ChooseAsync(string database)
    {
        if (_connection?.Server is not { } server) return;
        _syncing = true;
        Selected = database;
        _syncing = false;
        Notice = null;
        if (database == _connection.DatabaseLabel)
        {
            Session = null;
            changed();
            return;
        }
        IsOpening = true;
        changed();
        try
        {
            Session = await server.OpenAsync(database);
            if (Session == null) Notice = $"Database {database} could not be opened.";
        }
        finally
        {
            IsOpening = false;
            changed();
        }
    }
}
