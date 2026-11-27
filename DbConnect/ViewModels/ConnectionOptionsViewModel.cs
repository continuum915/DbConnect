using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public sealed record AuthenticationChoice(ConnectionAuthentication Mode, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The connection dialog's Options section: sign-in mode, timeouts, read-only, default schema, startup SQL and
/// extra driver properties (one key=value per line). Only the options the selected provider declares are shown.
/// </summary>
public sealed partial class ConnectionOptionsViewModel : ObservableObject
{
    private IConnectionOptionsDialect? _dialect;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesUsername), nameof(UsesPassword))]
    private AuthenticationChoice? _authentication;

    [ObservableProperty][NotifyPropertyChangedFor(nameof(Error))] private string _connectTimeout = string.Empty;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(Error))] private string _commandTimeout = string.Empty;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(Error))] private string _keepAlive = string.Empty;
    [ObservableProperty] private bool _isReadOnly;
    [ObservableProperty] private string _defaultSchema = string.Empty;
    [ObservableProperty] private string _startupSql = string.Empty;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(Error))] private string _additionalProperties = string.Empty;

    public IReadOnlyList<AuthenticationChoice> AuthenticationChoices =>
        (_dialect?.AuthenticationModes ?? []).Select(mode => new AuthenticationChoice(mode, Label(mode))).ToList();
    public bool HasAuthenticationChoice => AuthenticationChoices.Count > 1;
    public bool SupportsConnectTimeout => _dialect?.SupportsConnectTimeout == true;
    public bool SupportsKeepAlive => _dialect?.SupportsKeepAlive == true;
    public bool SupportsDefaultSchema => _dialect?.SupportsDefaultSchema == true;
    public string ReadOnlyEnforcement => _dialect?.ReadOnlyEnforcement ?? string.Empty;

    private ConnectionAuthentication Mode => Authentication?.Mode ?? ConnectionAuthentication.Password;
    public bool UsesUsername => Mode is not (ConnectionAuthentication.Integrated or ConnectionAuthentication.EntraIntegrated
        or ConnectionAuthentication.EntraDefault);
    public bool UsesPassword => Mode is ConnectionAuthentication.Password or ConnectionAuthentication.EntraPassword
        or ConnectionAuthentication.EntraServicePrincipal;

    public static string Label(ConnectionAuthentication mode) => mode switch
    {
        ConnectionAuthentication.Password => "User name and password",
        ConnectionAuthentication.Integrated => "Windows / integrated",
        ConnectionAuthentication.EntraInteractive => "Microsoft Entra – interactive (MFA)",
        ConnectionAuthentication.EntraIntegrated => "Microsoft Entra – integrated",
        ConnectionAuthentication.EntraPassword => "Microsoft Entra – password",
        ConnectionAuthentication.EntraServicePrincipal => "Microsoft Entra – service principal (client id / secret)",
        ConnectionAuthentication.EntraManagedIdentity => "Microsoft Entra – managed identity",
        ConnectionAuthentication.EntraDefault => "Microsoft Entra – default credential",
        _ => mode.ToString()
    };

    /// <summary>Switches to a provider's options, keeping values that still apply.</summary>
    public void SetProvider(string provider)
    {
        _dialect = ConnectionOptionDialects.For(provider);
        var mode = Mode;
        OnPropertyChanged(nameof(AuthenticationChoices));
        OnPropertyChanged(nameof(HasAuthenticationChoice));
        OnPropertyChanged(nameof(SupportsConnectTimeout));
        OnPropertyChanged(nameof(SupportsKeepAlive));
        OnPropertyChanged(nameof(SupportsDefaultSchema));
        OnPropertyChanged(nameof(ReadOnlyEnforcement));
        Authentication = AuthenticationChoices.FirstOrDefault(c => c.Mode == mode) ?? AuthenticationChoices.FirstOrDefault();
    }

    public void Load(ConnectionOptions? options)
    {
        options ??= new ConnectionOptions();
        Authentication = AuthenticationChoices.FirstOrDefault(c => c.Mode == options.Authentication) ?? AuthenticationChoices.FirstOrDefault();
        ConnectTimeout = options.ConnectTimeoutSeconds?.ToString() ?? string.Empty;
        CommandTimeout = options.CommandTimeoutSeconds?.ToString() ?? string.Empty;
        KeepAlive = options.KeepAliveSeconds?.ToString() ?? string.Empty;
        IsReadOnly = options.IsReadOnly;
        DefaultSchema = options.DefaultSchema ?? string.Empty;
        StartupSql = options.StartupSql ?? string.Empty;
        AdditionalProperties = string.Join(Environment.NewLine, options.AdditionalProperties.Select(p => $"{p.Key}={p.Value}"));
    }

    /// <summary>Why the options cannot be saved, or null.</summary>
    public string? Error
    {
        get
        {
            foreach (var (label, text) in new[] { ("Connect timeout", ConnectTimeout), ("Command timeout", CommandTimeout), ("Keep-alive", KeepAlive) })
                if (text.Trim().Length > 0 && (!int.TryParse(text.Trim(), out var value) || value < 0))
                    return $"{label} must be a whole number of seconds.";
            foreach (var line in Lines())
            {
                if (!line.Contains('=')) return $"\"{line}\" is not key=value.";
                if (ConnectionOptionDialects.IsSecretKey(line[..line.IndexOf('=')]))
                    return $"\"{line[..line.IndexOf('=')]}\" holds a secret; use the password field instead.";
            }
            return null;
        }
    }

    private IEnumerable<string> Lines() =>
        AdditionalProperties.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    private static int? Seconds(string text) => int.TryParse(text.Trim(), out var value) && value >= 0 ? value : null;

    public ConnectionOptions ToOptions() => new()
    {
        Authentication = Mode,
        ConnectTimeoutSeconds = SupportsConnectTimeout ? Seconds(ConnectTimeout) : null,
        CommandTimeoutSeconds = Seconds(CommandTimeout),
        KeepAliveSeconds = SupportsKeepAlive ? Seconds(KeepAlive) : null,
        IsReadOnly = IsReadOnly,
        DefaultSchema = SupportsDefaultSchema && DefaultSchema.Trim().Length > 0 ? DefaultSchema.Trim() : null,
        StartupSql = StartupSql.Trim().Length > 0 ? StartupSql : null,
        AdditionalProperties = Lines().Where(l => l.Contains('='))
            .Select(l => new ConnectionProperty { Key = l[..l.IndexOf('=')].Trim(), Value = l[(l.IndexOf('=') + 1)..].Trim() })
            .ToList()
    };
}
