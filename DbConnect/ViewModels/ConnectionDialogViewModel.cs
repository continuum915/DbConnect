using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class ConnectionDialogViewModel : ViewModelBase
{
    private readonly DatabaseService _dbService;
    private readonly ConnectionSettingsService _settingsService;
    private readonly ConnectionEstablisher _establisher;
    public SshTrustPrompt? TrustPrompt { get; set; }

    // ── Basic Fields ──────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _connectionName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFileProvider))]
    [NotifyPropertyChangedFor(nameof(IsNetworkProvider))]
    [NotifyPropertyChangedFor(nameof(ShowsVerifyFullSslHint))]
    [NotifyPropertyChangedFor(nameof(UsesEncryptionOptions))]
    [NotifyPropertyChangedFor(nameof(SslModes))]
    [NotifyPropertyChangedFor(nameof(HasCertificateSslModes))]
    [NotifyPropertyChangedFor(nameof(SupportsCaCertificate))]
    [NotifyPropertyChangedFor(nameof(SupportsClientCertificate))]
    [NotifyPropertyChangedFor(nameof(SupportsClientKey))]
    private string _selectedProvider = string.Empty;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>The last Test Connection outcome; its driver detail sits behind the dialog's "Details" expander.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestFailureDetail))]
    private ConnectionTestResult? _testResult;

    public bool HasTestFailureDetail => TestResult is { Success: false, Detail.Length: > 0 };

    public ObservableCollection<string> AvailableProviders { get; } = new();
    public ObservableCollection<ConnectionInfo> SavedConnections { get; } = new();

    /// <summary>Completes when saved connections and their groups have finished loading.</summary>
    public Task Initialization { get; }

    [ObservableProperty]
    private ConnectionInfo? _selectedSavedConnection;

    public ConnectionInfo? Result { get; set; }

    // ── Provider-Specific Visibility ─────────────────────────────────────────

    private IConnectionStringFormat? SelectedFormat => ConnectionStringFormats.For(SelectedProvider);
    public ConnectionOptionsViewModel Options { get; } = new();

    /// <summary>List every database on the server; the Database field is then the default one. Off pins the connection.</summary>
    [ObservableProperty] private bool _showAllDatabases = true;
    public bool SupportsServerDatabases => ServerDatabaseCatalogs.For(SelectedProvider) != null;

    public bool IsFileProvider => SelectedFormat?.UsesDatabaseFile == true;
    public bool IsNetworkProvider => SelectedFormat?.UsesDatabaseFile == false;
    private DatabaseProviderCapabilities? SelectedCapabilities =>
        _dbService.GetAvailableProviders().Contains(SelectedProvider, System.StringComparer.OrdinalIgnoreCase)
            ? _dbService.GetProvider(SelectedProvider).Capabilities : null;
    public bool ShowsVerifyFullSslHint => SelectedCapabilities?.ShowsVerifyFullSslHint == true;
    public bool UsesEncryptionOptions => SelectedCapabilities?.UsesEncryptionOptions == true;

    public IReadOnlyList<string> SslModes => SelectedFormat?.SslModes ?? System.Array.Empty<string>();
    public bool HasCertificateSslModes => SslModes.Count > 0;
    public bool SupportsCaCertificate => SelectedFormat?.SupportsCaCertificate == true;
    public bool SupportsClientCertificate => SelectedFormat?.SupportsClientCertificate == true;
    public bool SupportsClientKey => SelectedFormat?.SupportsClientKey == true;
    public IReadOnlyList<string> SqlServerSslModes { get; } = new[] { "Optional", "Required", "Strict" };

    // ── File Fields (SQLite / DuckDB) ─────────────────────────────────────────

    [ObservableProperty]
    private string _databaseFilePath = string.Empty;

    // ── Server Fields (PostgreSQL / SQL Server / MySQL) ───────────────────────

    [ObservableProperty]
    private string _host = string.Empty;

    [ObservableProperty]
    private string _port = string.Empty;

    [ObservableProperty]
    private string _database = string.Empty;

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    // ── SSL / TLS Fields (network providers only) ─────────────────────────────

    [ObservableProperty]
    private string _sslMode = "Prefer";

    [ObservableProperty]
    private string _sqlServerSslMode = "Optional";

    [ObservableProperty]
    private bool _trustServerCertificate;

    [ObservableProperty]
    private string _sslClientCertPath = string.Empty;

    [ObservableProperty]
    private string _sslCaCertPath = string.Empty;

    [ObservableProperty]
    private string _sslClientKeyPath = string.Empty;

    // ── SSH Tunnel Fields (network providers only) ────────────────────────────

    [ObservableProperty]
    private bool _useSshTunnel;

    [ObservableProperty]
    private string _sshHost = string.Empty;

    [ObservableProperty]
    private string _sshPort = "22";

    [ObservableProperty]
    private string _sshUsername = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SshAuthIsPassword))]
    [NotifyPropertyChangedFor(nameof(SshAuthIsPrivateKey))]
    private string _sshAuthMethod = "Password";

    [ObservableProperty]
    private string _sshPassword = string.Empty;

    [ObservableProperty]
    private string _sshPrivateKeyPath = string.Empty;

    [ObservableProperty]
    private string _sshPrivateKeyPassphrase = string.Empty;

    public IReadOnlyList<string> SshAuthMethods { get; } = new[] { "Password", "PrivateKey" };
    public bool SshAuthIsPassword => SshAuthMethod == "Password";
    public bool SshAuthIsPrivateKey => SshAuthMethod == "PrivateKey";

    // ── Group / Label Fields ──────────────────────────────────────────────────

    [ObservableProperty]
    private string _connectionGroup = string.Empty;

    [ObservableProperty]
    private LabelColorOption? _selectedLabelColor;

    public IReadOnlyList<LabelColorOption> LabelColorOptions { get; } = LabelColorOption.Defaults;

    /// <summary>Flat unique list of known group names, for the editable ComboBox.</summary>
    public ObservableCollection<string> KnownGroups { get; } = new();

    /// <summary>Grouped representation of saved connections (for the left panel).</summary>
    public ObservableCollection<ConnectionGroup> GroupedConnections { get; } = new();

    // ── Environment Tier ──────────────────────────────────────────────────────

    public IReadOnlyList<string> AvailableEnvironmentTiers { get; } = new[] { "Development", "Staging", "Production" };

    [ObservableProperty]
    private string _selectedEnvironmentTier = "Development";

    // ── Constructor ───────────────────────────────────────────────────────────

    public ConnectionDialogViewModel(DatabaseService dbService, ConnectionSettingsService settingsService, ConnectionEstablisher? establisher = null)
    {
        _dbService = dbService;
        _settingsService = settingsService;
        _establisher = establisher ?? new ConnectionEstablisher(dbService);

        foreach (var p in _dbService.GetAvailableProviders())
            AvailableProviders.Add(p);

        if (AvailableProviders.Count > 0)
            SelectedProvider = AvailableProviders[0];

        Initialization = LoadSavedConnectionsAsync();
    }

    public ConnectionDialogViewModel() : this(new DatabaseService(), new ConnectionSettingsService()) { }

    // ── Default Ports When Provider Changes ───────────────────────────────────

    partial void OnSelectedProviderChanged(string value)
    {
        Options.SetProvider(value);
        ShowAllDatabases = true;
        OnPropertyChanged(nameof(SupportsServerDatabases));
        Port = ConnectionStringFormats.For(value)?.DefaultPort?.ToString() ?? string.Empty;
        SslMode = SelectedFormat?.DefaultSslMode ?? "Prefer";
        SqlServerSslMode = SelectedFormat?.DefaultSslMode ?? "Required";
        TrustServerCertificate = false;
        SslClientCertPath = string.Empty;
        SslCaCertPath = string.Empty;
        SslClientKeyPath = string.Empty;
        UseSshTunnel = false;
        SshHost = string.Empty;
        SshPort = "22";
        SshUsername = string.Empty;
        SshAuthMethod = "Password";
        SshPassword = string.Empty;
        SshPrivateKeyPath = string.Empty;
        SshPrivateKeyPassphrase = string.Empty;
    }

    // ── Loading Saved Connections ─────────────────────────────────────────────

    private async Task LoadSavedConnectionsAsync()
    {
        var conns = await _settingsService.LoadConnectionsAsync();
        foreach (var c in conns)
            SavedConnections.Add(c);
        RebuildGroupedConnections();
    }

    /// <summary>
    /// Rebuilds <see cref="GroupedConnections"/> and <see cref="KnownGroups"/> from
    /// <see cref="SavedConnections"/>.  Called after every load/save/delete.
    /// </summary>
    private void RebuildGroupedConnections()
    {
        GroupedConnections.Clear();

        var grouped = SavedConnections
            .GroupBy(c => string.IsNullOrWhiteSpace(c.Group) ? string.Empty : c.Group)
            .OrderBy(g => string.IsNullOrEmpty(g.Key) ? 1 : 0) // ungrouped last
            .ThenBy(g => g.Key);

        foreach (var g in grouped)
        {
            var group = new ConnectionGroup
            {
                GroupName = string.IsNullOrEmpty(g.Key) ? "(Ungrouped)" : g.Key,
                IsExpanded = true
            };

            foreach (var conn in g)
                group.Connections.Add(conn);

            group.SelectionChanged = selected =>
            {
                // Clear other groups' selections
                foreach (var other in GroupedConnections)
                    if (!ReferenceEquals(other, group))
                        other.SelectedConnection = null;

                SelectedSavedConnection = selected;
            };

            GroupedConnections.Add(group);
        }

        // Rebuild KnownGroups
        KnownGroups.Clear();
        foreach (var name in SavedConnections
            .Select(c => c.Group)
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct()
            .OrderBy(g => g))
        {
            KnownGroups.Add(name);
        }
    }

    partial void OnSelectedSavedConnectionChanged(ConnectionInfo? value)
    {
        if (value == null) return;

        ConnectionName = value.Name;
        SelectedProvider = value.Provider;

        // Parse the saved connection string back into the individual fields
        PopulateFieldsFromConnectionString(value.Provider, value.ConnectionString);

        TrustServerCertificate = value.TrustServerCertificate;

        // Older saved connections kept certificate paths only in metadata.
        SslClientCertPath = value.SslClientCertPath ?? SslClientCertPath;
        SslCaCertPath = value.SslCaCertPath ?? SslCaCertPath;
        SslClientKeyPath = value.SslClientKeyPath ?? SslClientKeyPath;

        // SSH tunnel fields
        var tunnel = value.SshTunnel;
        if (tunnel != null)
        {
            UseSshTunnel = tunnel.Enabled;
            SshHost = tunnel.SshHost;
            SshPort = tunnel.SshPort.ToString();
            SshUsername = tunnel.SshUsername;
            SshAuthMethod = tunnel.AuthMethod;
            SshPrivateKeyPath = tunnel.PrivateKeyPath;
            SshPassword = tunnel.SshPassword; // restored by P2-032 credential store
            SshPrivateKeyPassphrase = tunnel.PrivateKeyPassphrase;
        }
        else
        {
            UseSshTunnel = false;
            SshHost = string.Empty;
            SshPort = "22";
            SshUsername = string.Empty;
            SshAuthMethod = "Password";
            SshPrivateKeyPath = string.Empty;
            SshPassword = string.Empty;
            SshPrivateKeyPassphrase = string.Empty;
        }

        // Group / label fields
        ConnectionGroup = value.Group ?? string.Empty;
        SelectedLabelColor = LabelColorOptions.FirstOrDefault(o => o.Hex == value.LabelColor)
                             ?? LabelColorOptions[0];

        // Environment tier
        SelectedEnvironmentTier = value.EnvironmentTier ?? "Development";
        Options.Load(value.Options);
        ShowAllDatabases = value.ShowAllDatabases;

        StatusMessage = "Loaded connection.";
    }

    private void PopulateFieldsFromConnectionString(string provider, string connectionString)
    {
        if (ConnectionStringFormats.For(provider) is not { } format)
            return;

        var fields = format.Parse(connectionString);
        DatabaseFilePath = fields.FilePath;
        Host = fields.Host;
        Port = fields.Port?.ToString() ?? string.Empty;
        Database = fields.Database;
        Username = fields.Username;
        Password = fields.Password;
        SslClientCertPath = fields.SslClientCertPath;
        SslCaCertPath = fields.SslCaCertPath;
        SslClientKeyPath = fields.SslClientKeyPath;
        TrustServerCertificate = fields.TrustServerCertificate;
        if (UsesEncryptionOptions)
            SqlServerSslMode = fields.SslMode ?? "Optional";
        else if (HasCertificateSslModes)
            SslMode = fields.SslMode ?? "Prefer";
    }

    // ── Build Connection String From Fields ───────────────────────────────────

    private string BuildConnectionString() =>
        SelectedFormat?.Build(new ConnectionFields
        {
            FilePath = DatabaseFilePath,
            Host = Host,
            Port = int.TryParse(Port, out var port) ? port : null,
            Database = Database,
            Username = Username,
            Password = Password,
            SslMode = UsesEncryptionOptions ? SqlServerSslMode : HasCertificateSslModes ? SslMode : null,
            SslCaCertPath = SupportsCaCertificate ? SslCaCertPath : string.Empty,
            SslClientCertPath = SupportsClientCertificate ? SslClientCertPath : string.Empty,
            SslClientKeyPath = SupportsClientKey ? SslClientKeyPath : string.Empty,
            TrustServerCertificate = TrustServerCertificate
        }) ?? string.Empty;

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        TestResult = null;
        StatusMessage = "Testing...";
        var connection = BuildConnection();

        if (string.IsNullOrWhiteSpace(SelectedProvider) || string.IsNullOrWhiteSpace(connection.ConnectionString))
        {
            StatusMessage = "Please fill in the required connection fields.";
            return;
        }

        ConnectionTestResult result;
        try
        {
            result = await _establisher.TestAsync(connection, cancellationToken, TrustPrompt);
        }
        catch (System.OperationCanceledException)
        {
            StatusMessage = "Test cancelled.";
            return;
        }
        TestResult = result;
        StatusMessage = result.Success ? $"✅ {result.Summary}" : $"❌ {result.CategoryLabel}: {result.Summary}";
    }

    [RelayCommand]
    private async Task SaveConnectionAsync()
    {
        await Initialization;

        if (string.IsNullOrWhiteSpace(ConnectionName))
        {
            StatusMessage = "Please enter a connection name.";
            return;
        }

        var connStr = BuildConnectionString();
        if (string.IsNullOrWhiteSpace(connStr))
        {
            StatusMessage = "Please fill in the required connection fields.";
            return;
        }
        if (Options.Error is { } optionsError)
        {
            StatusMessage = optionsError;
            return;
        }

        var existing = SelectedSavedConnection ?? SavedConnections.FirstOrDefault(c => c.Name == ConnectionName);
        if (SavedConnections.Any(c => c != existing && c.Name == ConnectionName))
        {
            StatusMessage = "A connection with this name already exists.";
            return;
        }
        if (existing != null)
        {
            existing.Name = ConnectionName;
            existing.Provider = SelectedProvider;
            existing.ConnectionString = connStr;
            ApplySslFields(existing);
            ApplySshFields(existing);
            ApplyGroupFields(existing);
        }
        else
        {
            var newConn = new ConnectionInfo
            {
                Name = ConnectionName,
                Provider = SelectedProvider,
                ConnectionString = connStr
            };
            ApplySslFields(newConn);
            ApplySshFields(newConn);
            ApplyGroupFields(newConn);
            SavedConnections.Add(newConn);
            SelectedSavedConnection = newConn;
        }

        await _settingsService.SaveConnectionsAsync(SavedConnections);
        RebuildGroupedConnections();
        StatusMessage = "Connection saved successfully.";
    }

    [RelayCommand]
    private async Task DeleteConnectionAsync()
    {
        await Initialization;

        if (SelectedSavedConnection != null)
        {
            var deleted = SelectedSavedConnection;
            SavedConnections.Remove(SelectedSavedConnection);
            await _settingsService.DeleteConnectionAsync(deleted);
            await _settingsService.SaveConnectionsAsync(SavedConnections);
            RebuildGroupedConnections();

            ConnectionName = string.Empty;
            DatabaseFilePath = string.Empty;
            Host = string.Empty;
            Port = string.Empty;
            Database = string.Empty;
            Username = string.Empty;
            Password = string.Empty;
            SslMode = SelectedFormat?.DefaultSslMode ?? "Prefer";
            SqlServerSslMode = SelectedFormat?.DefaultSslMode ?? "Required";
            TrustServerCertificate = false;
            SslClientCertPath = string.Empty;
            SslCaCertPath = string.Empty;
            SslClientKeyPath = string.Empty;
            UseSshTunnel = false;
            SshHost = string.Empty;
            SshPort = "22";
            SshUsername = string.Empty;
            SshAuthMethod = "Password";
            SshPassword = string.Empty;
            SshPrivateKeyPath = string.Empty;
            SshPrivateKeyPassphrase = string.Empty;
            SelectedSavedConnection = null;
            StatusMessage = "Connection deleted.";
        }
    }

    [RelayCommand]
    private void DuplicateConnection()
    {
        if (SelectedSavedConnection == null) return;

        var copy = new ConnectionInfo
        {
            Name = SelectedSavedConnection.Name + " (copy)",
            Provider = SelectedSavedConnection.Provider,
            ConnectionString = SelectedSavedConnection.ConnectionString,
            Options = SelectedSavedConnection.Options?.Copy() ?? new()
        };
        SavedConnections.Add(copy);
        SelectedSavedConnection = copy;
        ConnectionName = copy.Name;
        PopulateFieldsFromConnectionString(copy.Provider, copy.ConnectionString);
        StatusMessage = "Connection duplicated.";
    }

    public void SetResult() => Result = BuildConnection();

    /// <summary>The connection the dialog's fields describe, as Test and Connect hand it to the establisher.</summary>
    private ConnectionInfo BuildConnection()
    {
        var connection = new ConnectionInfo
        {
            Id = SelectedSavedConnection?.Id ?? System.Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(ConnectionName) ? "New Connection" : ConnectionName,
            Provider = SelectedProvider,
            ConnectionString = BuildConnectionString()
        };
        ApplySslFields(connection);
        ApplySshFields(connection);
        ApplyGroupFields(connection);
        return connection;
    }

    private void ApplySslFields(ConnectionInfo target)
    {
        target.SslMode = UsesEncryptionOptions ? SqlServerSslMode : HasCertificateSslModes ? SslMode : null;
        target.SslClientCertPath = SupportsClientCertificate && !string.IsNullOrWhiteSpace(SslClientCertPath) ? SslClientCertPath : null;
        target.SslCaCertPath = SupportsCaCertificate && !string.IsNullOrWhiteSpace(SslCaCertPath) ? SslCaCertPath : null;
        target.SslClientKeyPath = SupportsClientKey && !string.IsNullOrWhiteSpace(SslClientKeyPath) ? SslClientKeyPath : null;
        target.TrustServerCertificate = TrustServerCertificate;
    }

    private void ApplySshFields(ConnectionInfo target)
    {
        if (!IsNetworkProvider || !UseSshTunnel)
        {
            target.SshTunnel = null;
            return;
        }

        target.SshTunnel = new Models.SshTunnelConfig
        {
            Enabled = true,
            SshHost = SshHost,
            SshPort = int.TryParse(SshPort, out var p) ? p : 22,
            SshUsername = SshUsername,
            AuthMethod = SshAuthMethod,
            PrivateKeyPath = SshPrivateKeyPath,
            SshPassword = SshPassword,
            PrivateKeyPassphrase = SshPrivateKeyPassphrase
        };
    }

    private void ApplyGroupFields(ConnectionInfo target)
    {
        target.Group = ConnectionGroup ?? string.Empty;
        target.LabelColor = SelectedLabelColor?.Hex is { Length: > 0 } hex ? hex : null;
        target.EnvironmentTier = SelectedEnvironmentTier;
        target.Options = Options.ToOptions();
        target.ShowAllDatabases = SupportsServerDatabases && ShowAllDatabases;
    }
}
