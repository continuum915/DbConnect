using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>
/// The status bar's derived text: the active connection (name, server version, encoding), the breadcrumb of the active
/// object, and whether the active tab holds an open transaction. Parts that are not known show "—".
/// </summary>
public sealed partial class StatusBarState : ObservableObject
{
    public const string Unknown = "—";

    private readonly Func<DatabaseSession?> _activeSession;
    private readonly Func<ViewModelBase?> _activeTab;
    private readonly Dictionary<string, string> _serverVersions = new(StringComparer.Ordinal);

    [ObservableProperty]
    private string _connectionText = "No connection";

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _breadcrumb = string.Empty;

    [ObservableProperty]
    private bool _hasOpenTransaction;

    /// <summary>"Ln, Col · encoding · line endings" while a SQL editor has focus; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCaretText))]
    private string? _caretText;

    public bool HasCaretText => !string.IsNullOrEmpty(CaretText);

    public void SetCaretText(string? text) => CaretText = text;

    public StatusBarState(Func<DatabaseSession?> activeSession, Func<ViewModelBase?> activeTab)
    {
        _activeSession = activeSession;
        _activeTab = activeTab;
    }

    /// <summary>Recomputes every part from the active tab and its session.</summary>
    public void Refresh()
    {
        var session = _activeSession();
        var tab = _activeTab();
        IsConnected = session != null;
        HasOpenTransaction = tab is QueryTabViewModel { IsInTransaction: true };
        ConnectionText = session == null ? "No connection" : ConnectionLine(session);
        Breadcrumb = BreadcrumbFor(session, tab);
        if (session != null && !_serverVersions.ContainsKey(session.Name))
            _ = LoadServerVersionAsync(session);
    }

    private string ConnectionLine(DatabaseSession session)
    {
        var version = _serverVersions.TryGetValue(session.Name, out var known) && known.Length > 0 ? known : Unknown;
        return $"{session.Name} · {version} · {Unknown}";
    }

    private static string BreadcrumbFor(DatabaseSession? session, ViewModelBase? tab)
    {
        if (tab == null)
            return string.Empty;
        var scope = session?.DatabaseLabel is { Length: > 0 } database ? database : session?.Name;
        var path = scope == null ? tab.Title : $"{scope} › {tab.Title}";
        return tab.IsDirty ? $"{path} (unsaved changes)" : path;
    }

    private async Task LoadServerVersionAsync(DatabaseSession session)
    {
        _serverVersions[session.Name] = string.Empty;
        try
        {
            var version = await session.Provider.GetServerVersionAsync(session.ConnectionString);
            // Providers name themselves in the version ("SQLite 3.46.1", "MySQL 8.0.36").
            _serverVersions[session.Name] = version.Trim();
        }
        catch
        {
            // The version is informational; leave it unknown.
            return;
        }

        if (ReferenceEquals(_activeSession(), session))
            ConnectionText = ConnectionLine(session);
    }
}
