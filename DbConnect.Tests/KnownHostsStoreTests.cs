using System.Text.Json;
using DbConnect.Services;

namespace DbConnect.Tests;

public sealed class KnownHostsStoreTests
{
    [Fact]
    public void ReloadPreservesHostPortAlgorithmAndFingerprintAndSeparatesPorts()
    {
        using var context = new SshTrustTestContext();
        var key = context.Transport.Key;
        Assert.True(context.Store.Save(key, null));
        var otherPort = key with { Port = 2222, Algorithm = "ssh-rsa", Sha256Fingerprint = "SHA256:other-port" };
        Assert.True(context.Store.Save(otherPort, null));
        var reloaded = new KnownHostsStore(context.DirectoryPath);
        Assert.Equal(key, reloaded.Find("BASTION.EXAMPLE", 22));
        Assert.Equal(otherPort, reloaded.Find("bastion.example", 2222));
        Assert.Null(reloaded.Find("another.example", 22));
        Assert.Null(reloaded.Find("bastion.example", 2223));
    }

    [Fact]
    public void StaleReplacementCannotOverwriteADifferentTrustedKey()
    {
        using var context = new SshTrustTestContext();
        var original = context.Transport.Key;
        Assert.True(context.Store.Save(original, null));
        var secondStore = new KnownHostsStore(context.DirectoryPath);
        var reviewed = secondStore.Find(original.Host, original.Port);
        var newer = original with { Sha256Fingerprint = "SHA256:newer" };
        Assert.True(context.Store.Save(newer, original));
        Assert.False(secondStore.Save(original with { Sha256Fingerprint = "SHA256:stale" }, reviewed));
        Assert.Equal(newer, new KnownHostsStore(context.DirectoryPath).Find(original.Host, original.Port));
    }

    [Fact]
    public void InvalidStoreIsReportedRatherThanForgotten()
    {
        using var context = new SshTrustTestContext();
        File.WriteAllText(Path.Combine(context.DirectoryPath, "ssh-known-hosts.json"), "not json");
        Assert.Throws<JsonException>(() => context.Store.Find("bastion.example", 22));
    }
}
