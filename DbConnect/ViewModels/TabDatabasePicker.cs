using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>
/// The query tab's database picker for server connections that show all databases. Picking a database moves
/// the tab to that database's session (each database has its own session, so this works on every provider),
/// and is refused while a transaction is open or a run is in progress. After a batch that runs USE, the tab
/// shows the database the session is really in.
/// </summary>
public sealed partial class TabDatabasePicker : ObservableObject
{
    private readonly QueryTabViewModel _tab;
    private bool _syncing;

    public ObservableCollection<string> Databases { get; } = new();
    [ObservableProperty] private string? _selectedDatabase;
    [ObservableProperty] private bool _isAvailable;
    [ObservableProperty] private string? _notice;
    /// <summary>The database a USE moved the session to, when it differs from the picker.</summary>
    [ObservableProperty] private string? _inSessionDatabase;

    public TabDatabasePicker(QueryTabViewModel tab)
    {
        _tab = tab;
        tab.PropertyChanged += OnTabChanged;
        Refresh();
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QueryTabViewModel.SelectedSession)) Refresh();
    }

    public void Refresh()
    {
        _syncing = true;
        try
        {
            var server = _tab.SelectedSession?.Server;
            IsAvailable = server != null;
            Databases.Clear();
            foreach (var database in server?.Databases ?? []) Databases.Add(database);
            SelectedDatabase = _tab.SelectedSession?.DatabaseLabel;
            InSessionDatabase = null;
        }
        finally { _syncing = false; }
    }

    partial void OnSelectedDatabaseChanged(string? value)
    {
        if (_syncing || value == null || _tab.SelectedSession is not { Server: { } server } current ||
            value == current.DatabaseLabel) return;
        _ = SwitchAsync(server, value);
    }

    public async Task SwitchAsync(ServerDatabaseDirectory server, string database)
    {
        Notice = null;
        if (_tab.IsInTransaction || _tab.IsExecuting)
        {
            Notice = "Commit or roll back the open transaction before switching database.";
            Refresh();
            return;
        }
        var session = await server.OpenAsync(database);
        if (session == null) Notice = $"Database {database} could not be opened.";
        else _tab.SelectedSession = session;
        Refresh();
    }

    /// <summary>After a batch containing USE, reads where the session is now.</summary>
    public async Task ObserveAsync(IDbSession session, string sql, SqlBatchDialect dialect)
    {
        if (_tab.SelectedSession?.Server?.CurrentDatabaseSql is not { } query ||
            !SqlBatchParser.SplitStatements(sql, dialect).Any(IsUse)) return;
        var result = await session.ExecuteQueryAsync(query);
        if (result.HasError || result.ObjectRows.Count == 0) return;
        var current = Convert.ToString(result.ObjectRows[0][0]);
        InSessionDatabase = current == _tab.SelectedSession?.DatabaseLabel ? null : current;
    }

    private static bool IsUse(string statement)
    {
        var trimmed = statement.TrimStart();
        return trimmed.StartsWith("USE", StringComparison.OrdinalIgnoreCase) &&
               (trimmed.Length == 3 || char.IsWhiteSpace(trimmed[3]) || trimmed[3] is '[' or '`' or '"');
    }
}
