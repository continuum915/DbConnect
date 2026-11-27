using System;
using System.IO;
using DbConnect.Models;
using DbConnect.Services;
using FakeProvider = DbConnect.Tests.ConnectionEstablisherTests.FakeProvider;

namespace DbConnect.Tests;

public class ConnectionCertificateFileTests
{
    [Theory]
    [InlineData("ca")]
    [InlineData("cert")]
    [InlineData("key")]
    public async Task Test_MissingCertificateFile_NamesThePathAndDoesNotReachTheDatabase(string field)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pem");
        var settings = Connection();
        SetPath(settings, field, path);
        var provider = new FakeProvider("PostgreSQL");

        var result = await Establisher(provider).TestAsync(settings);

        Assert.Equal(ConnectionFailureCategory.CertificateFileMissing, result.Category);
        Assert.Contains(path, result.Summary);
        Assert.Empty(provider.TestedConnectionStrings);
    }

    [Theory]
    [InlineData("PostgreSQL", "ca")]
    [InlineData("PostgreSQL", "cert")]
    [InlineData("PostgreSQL", "key")]
    [InlineData("MySQL", "ca")]
    [InlineData("MySQL", "cert")]
    [InlineData("MySQL", "key")]
    public async Task Test_CertificatePathInConnectionString_IsChecked(string providerName, string field)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pem");
        var fields = new ConnectionFields
        {
            Host = "db.internal",
            SslCaCertPath = field == "ca" ? path : "",
            SslClientCertPath = field == "cert" ? path : "",
            SslClientKeyPath = field == "key" ? path : ""
        };
        var settings = new ConnectionInfo
        {
            Provider = providerName,
            ConnectionString = ConnectionStringFormats.For(providerName)!.Build(fields)
        };

        var result = await Establisher(new FakeProvider(providerName)).TestAsync(settings);

        Assert.Equal(ConnectionFailureCategory.CertificateFileMissing, result.Category);
        Assert.Contains(path, result.Summary);
    }

    [Theory]
    [InlineData("ca")]
    [InlineData("cert")]
    [InlineData("key")]
    public async Task Connect_MissingCertificateFile_ReportsTheCategoryAndClosesTheTunnel(string field)
    {
        var settings = Connection();
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pem");
        SetPath(settings, field, path);
        settings.SshTunnel = new SshTunnelConfig { Enabled = true, SshHost = "bastion" };
        var ssh = new FakeSsh();
        var provider = new FakeProvider("PostgreSQL");

        var error = await Assert.ThrowsAsync<CertificateFileException>(
            () => Establisher(provider, ssh).EstablishAsync(settings));

        Assert.Equal(ConnectionFailureCategory.CertificateFileMissing, error.Category);
        Assert.Contains(path, error.Message);
        Assert.True(ssh.Tunnel.IsClosed);
        Assert.Empty(provider.SchemaConnectionStrings);
    }

    [Fact]
    public async Task Test_UnreadableFile_ClosesTheTunnelAndNamesThePath()
    {
        var path = Path.GetTempFileName();
        try
        {
            using var locked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var settings = Connection();
            settings.SslCaCertPath = path;
            settings.SshTunnel = new SshTunnelConfig { Enabled = true, SshHost = "bastion" };
            var ssh = new FakeSsh();

            var result = await Establisher(new FakeProvider("PostgreSQL"), ssh).TestAsync(settings);

            Assert.Equal(ConnectionFailureCategory.CertificateFileMissing, result.Category);
            Assert.Contains(path, result.Summary);
            Assert.True(ssh.Tunnel.IsClosed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TestAndConnect_NoPathsOrReadableFiles_SucceedAndReleaseFileHandles(bool configureFiles)
    {
        var path = Path.GetTempFileName();
        try
        {
            var settings = Connection();
            if (configureFiles)
            {
                settings.SslCaCertPath = path;
                settings.SslClientCertPath = path;
                settings.SslClientKeyPath = path;
            }
            var establisher = Establisher(new FakeProvider("PostgreSQL"));

            var result = await establisher.TestAsync(settings);
            using var established = await establisher.EstablishAsync(settings);

            Assert.True(result.Success);
            Assert.Equal(settings.ConnectionString, established.Settings.ConnectionString);
            using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class FakeSsh : ISshTransport
    {
        public FakeTunnel Tunnel { get; } = new();
        public Task<ISshTunnel> OpenAsync(SshTunnelRequest request, System.Threading.CancellationToken cancellationToken) =>
            Task.FromResult<ISshTunnel>(Tunnel);
    }

    private sealed class FakeTunnel : ISshTunnel
    {
        public int LocalPort => 40000;
        public bool IsClosed { get; private set; }
        public void Dispose() => IsClosed = true;
    }

    private static ConnectionInfo Connection() => new()
    {
        Provider = "PostgreSQL",
        ConnectionString = "Host=db.internal;Database=app;Username=u"
    };

    private static void SetPath(ConnectionInfo settings, string field, string path)
    {
        switch (field)
        {
            case "ca": settings.SslCaCertPath = path; break;
            case "cert": settings.SslClientCertPath = path; break;
            case "key": settings.SslClientKeyPath = path; break;
        }
    }

    private static ConnectionEstablisher Establisher(FakeProvider provider, ISshTransport? ssh = null)
    {
        var databases = new DatabaseService();
        databases.RegisterProvider(provider);
        return new ConnectionEstablisher(databases, ssh);
    }
}
