using System;
using System.Threading;
using System.Threading.Tasks;

namespace DbConnect.Services;

public enum SshTrustDecision { Decline, TrustOnce, TrustAlways, ReplaceTrustedKey }

/// <summary>A changed key requires replacement; ordinary trust choices never accept it.</summary>
public sealed record SshTrustRequest(SshHostKey PresentedKey, SshHostKey? TrustedKey)
{
    public bool IsMismatch => TrustedKey is not null;
}

public delegate Task<SshTrustDecision> SshTrustPrompt(SshTrustRequest request, CancellationToken cancellationToken);

/// <summary>Fail-closed host-key policy shared by interactive connections and unattended callers.</summary>
public sealed class SshHostTrust(KnownHostsStore store)
{
    public bool Accept(SshHostKey key, SshTrustPrompt? prompt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var trusted = store.Find(key.Host, key.Port);
        if (trusted is not null && trusted.Algorithm == key.Algorithm && trusted.Sha256Fingerprint == key.Sha256Fingerprint)
            return true;
        if (prompt is null)
            return false;

        var decision = prompt(new SshTrustRequest(key, trusted), cancellationToken)
            .WaitAsync(cancellationToken).GetAwaiter().GetResult();
        cancellationToken.ThrowIfCancellationRequested();
        if (trusted is not null)
        {
            if (decision != SshTrustDecision.ReplaceTrustedKey)
                return false;
        }
        else if (decision == SshTrustDecision.TrustOnce)
            return true;
        else if (decision != SshTrustDecision.TrustAlways)
            return false;

        // Compare with the key displayed in the prompt, so a concurrent decision cannot be overwritten silently.
        return store.Save(key, trusted);
    }
}
