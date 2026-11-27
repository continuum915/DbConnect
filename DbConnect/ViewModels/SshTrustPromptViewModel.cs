using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class SshTrustPromptViewModel(SshTrustRequest request) : ViewModelBase
{
    public bool IsMismatch => request.IsMismatch;
    public bool IsUnknown => !IsMismatch;
    public string Heading => IsMismatch ? "WARNING: SSH host key changed" : "Unknown SSH host";
    public string Warning => IsMismatch
        ? "The connection is blocked. A changed key can indicate an impersonated server. Verify both fingerprints independently before replacing the trusted key."
        : "Verify this fingerprint independently before trusting this server.";
    public string Endpoint => $"{request.PresentedKey.Host}:{request.PresentedKey.Port}";
    public string Algorithm => request.PresentedKey.Algorithm;
    public string Fingerprint => request.PresentedKey.Sha256Fingerprint;
    public string? OldFingerprint => request.TrustedKey?.Sha256Fingerprint;
    public string? OldAlgorithm => request.TrustedKey?.Algorithm;
    public Action<SshTrustDecision>? Complete { get; set; }

    [RelayCommand]
    private void Decline() => Complete?.Invoke(SshTrustDecision.Decline);
    [RelayCommand]
    private void TrustOnce() { if (IsUnknown) Complete?.Invoke(SshTrustDecision.TrustOnce); }
    [RelayCommand]
    private void TrustAlways() { if (IsUnknown) Complete?.Invoke(SshTrustDecision.TrustAlways); }
    [RelayCommand]
    private void ReplaceTrustedKey() { if (IsMismatch) Complete?.Invoke(SshTrustDecision.ReplaceTrustedKey); }
}
