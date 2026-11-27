using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public sealed class SshHostTrustTests
{
    [Fact]
    public async Task UnknownHostWithoutPromptRejectsBeforeDatabaseAndLeavesNothingOpen()
    {
        using var context = new SshTrustTestContext();
        var result = await context.Establisher.TestAsync(context.Connection);
        Assert.Equal(ConnectionFailureCategory.SshHostKeyRejected, result.Category);
        Assert.Empty(context.Provider.TestedConnectionStrings);
        Assert.Equal(0, context.Transport.ActiveSessions);
        Assert.Equal(0, context.Transport.ActiveForwards);
    }
    [Theory]
    [InlineData(SshTrustDecision.TrustOnce, true, false)]
    [InlineData(SshTrustDecision.TrustAlways, true, true)]
    [InlineData(SshTrustDecision.Decline, false, false)]
    [InlineData(SshTrustDecision.ReplaceTrustedKey, false, false)]
    public async Task UnknownHostHonoursTheDecisionAndOnlyTrustAlwaysPersists(SshTrustDecision decision, bool accepted, bool persisted)
    {
        using var context = new SshTrustTestContext();
        var result = await context.Establisher.TestAsync(context.Connection, trustPrompt: (request, _) =>
        {
            Assert.Null(request.TrustedKey);
            Assert.Equal("bastion.example", request.PresentedKey.Host);
            Assert.Equal(22, request.PresentedKey.Port);
            Assert.Equal("ssh-ed25519", request.PresentedKey.Algorithm);
            Assert.Equal("SHA256:presented-key", request.PresentedKey.Sha256Fingerprint);
            return Task.FromResult(decision);
        });
        Assert.Equal(accepted, result.Success);
        if (!accepted) Assert.Equal(ConnectionFailureCategory.SshHostKeyRejected, result.Category);
        Assert.Equal(persisted ? context.Transport.Key : null, new KnownHostsStore(context.DirectoryPath).Find("bastion.example", 22));
        Assert.Equal(accepted ? 1 : 0, context.Provider.TestedConnectionStrings.Count);
        Assert.Equal(0, context.Transport.ActiveSessions);
        Assert.Equal(0, context.Transport.ActiveForwards);
    }

    [Fact]
    public async Task TrustedHostAfterRestartConnectsSilentlyAndDisposalClosesTheTunnel()
    {
        using var context = new SshTrustTestContext();
        Assert.True((await context.Establisher.TestAsync(context.Connection,
            trustPrompt: (_, _) => Task.FromResult(SshTrustDecision.TrustAlways))).Success);
        var restarted = new ConnectionEstablisher(context.Databases, context.Transport, new KnownHostsStore(context.DirectoryPath));
        using (await restarted.EstablishAsync(context.Connection, trustPrompt: (_, _) => throw new InvalidOperationException("Matching keys must not prompt")))
        {
            Assert.Equal(1, context.Transport.ActiveSessions);
            Assert.Equal(1, context.Transport.ActiveForwards);
        }
        Assert.Equal(0, context.Transport.ActiveSessions);
        Assert.Equal(0, context.Transport.ActiveForwards);
    }

    [Theory]
    [InlineData(SshTrustDecision.Decline, false)]
    [InlineData(SshTrustDecision.TrustOnce, false)]
    [InlineData(SshTrustDecision.TrustAlways, false)]
    [InlineData(SshTrustDecision.ReplaceTrustedKey, true)]
    public async Task ChangedKeyRequiresExplicitReplacementAndShowsBothFingerprints(SshTrustDecision decision, bool accepted)
    {
        using var context = new SshTrustTestContext();
        var old = context.Transport.Key;
        Assert.True(context.Store.Save(old, null));
        context.Transport.Key = old with { Sha256Fingerprint = "SHA256:replacement-key" };
        var result = await context.Establisher.TestAsync(context.Connection, trustPrompt: (request, _) =>
        {
            Assert.True(request.IsMismatch);
            Assert.Equal("SHA256:presented-key", request.TrustedKey!.Sha256Fingerprint);
            Assert.Equal("SHA256:replacement-key", request.PresentedKey.Sha256Fingerprint);
            return Task.FromResult(decision);
        });
        Assert.Equal(accepted, result.Success);
        if (!accepted) Assert.Equal(ConnectionFailureCategory.SshHostKeyRejected, result.Category);
        Assert.Equal(accepted ? context.Transport.Key : old, new KnownHostsStore(context.DirectoryPath).Find(old.Host, old.Port));
        Assert.Equal(0, context.Transport.ActiveSessions);
        Assert.Equal(0, context.Transport.ActiveForwards);
    }

    [Fact]
    public async Task ChangedKeyWithoutPromptRejectsEstablishmentAndRetainsTrust()
    {
        using var context = new SshTrustTestContext();
        var old = context.Transport.Key;
        Assert.True(context.Store.Save(old, null));
        context.Transport.Key = old with { Algorithm = "ssh-rsa" };
        var error = await Assert.ThrowsAsync<SshTunnelException>(() => context.Establisher.EstablishAsync(context.Connection));
        Assert.Equal(ConnectionFailureCategory.SshHostKeyRejected, error.Category);
        Assert.Equal(old, context.Store.Find(old.Host, old.Port));
        Assert.Empty(context.Provider.SchemaConnectionStrings);
        Assert.Equal(0, context.Transport.ActiveSessions);
        Assert.Equal(0, context.Transport.ActiveForwards);
    }

    [Fact]
    public async Task CancellationWhilePromptIsPendingLeavesNothingOpenOrPersisted()
    {
        using var context = new SshTrustTestContext();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<SshTrustDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var test = context.Establisher.TestAsync(context.Connection, cancellation.Token, (_, _) =>
        {
            entered.SetResult();
            return pending.Task;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.WaitAsync(TimeSpan.FromSeconds(5)));
        pending.SetResult(SshTrustDecision.TrustAlways);
        Assert.Null(context.Store.Find("bastion.example", 22));
        Assert.Empty(context.Provider.TestedConnectionStrings);
        Assert.Equal(0, context.Transport.ActiveSessions);
        Assert.Equal(0, context.Transport.ActiveForwards);
    }
}
