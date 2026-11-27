using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using DbConnect.Models;
using DbConnect.Services;
using Renci.SshNet.Common;
using FakeProvider = DbConnect.Tests.ConnectionEstablisherTests.FakeProvider;

namespace DbConnect.Tests;

/// <summary>milestone-0 ticket 03 — the establisher opens, rewrites to and closes the SSH tunnel.</summary>
public class ConnectionEstablisherTunnelTests
{
    private const string Remote = "Host=db.internal;Port=6543;Database=app;Username=u;Password=p";

    [Fact]
    public async Task Test_ThroughATunnel_RewritesTheEndpointAndClosesTheTunnelAfterwards()
    {
        var provider = new FakeProvider("PostgreSQL");
        var ssh = new FakeSshTransport { LocalPort = 40123 };

        var result = await Establisher(provider, ssh).TestAsync(Tunnelled());

        Assert.True(result.Success);
        var request = Assert.Single(ssh.Requests);
        Assert.Equal(("db.internal", 6543), (request.RemoteHost, request.RemotePort));
        Assert.Equal("bastion.example", request.Config.SshHost);
        var tested = Assert.Single(provider.TestedConnectionStrings);
        Assert.Contains("Host=127.0.0.1", tested);
        Assert.Contains("Port=40123", tested);
        Assert.True(Assert.Single(ssh.Opened).IsClosed);
    }

    [Fact]
    public async Task Test_DatabaseStepFails_ClosesTheTunnel()
    {
        var provider = new FakeProvider("PostgreSQL")
        {
            TestResult = ConnectionTestResult.Failed(ConnectionFailureCategory.Authentication, "bad password", TimeSpan.Zero)
        };
        var ssh = new FakeSshTransport();

        var result = await Establisher(provider, ssh).TestAsync(Tunnelled());

        Assert.Equal(ConnectionFailureCategory.Authentication, result.Category);
        Assert.True(Assert.Single(ssh.Opened).IsClosed);
    }

    [Fact]
    public async Task Establish_SchemaLoadFailsAfterTheTunnelOpens_ClosesTheTunnel()
    {
        var provider = new FakeProvider("PostgreSQL") { SchemaError = new InvalidOperationException("schema unavailable") };
        var ssh = new FakeSshTransport();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Establisher(provider, ssh).EstablishAsync(Tunnelled()));

        Assert.True(Assert.Single(ssh.Opened).IsClosed);
    }

    [Fact]
    public async Task Establish_KeepsTheTunnelOpenWithAKeepAliveUntilDisposed()
    {
        var provider = new FakeProvider("PostgreSQL");
        var ssh = new FakeSshTransport { LocalPort = 40124 };

        var established = await Establisher(provider, ssh).EstablishAsync(Tunnelled());

        var tunnel = Assert.Single(ssh.Opened);
        Assert.False(tunnel.IsClosed);
        Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(ssh.Requests).KeepAliveInterval);
        Assert.Contains("Port=40124", established.Settings.ConnectionString);
        Assert.Contains("Port=40124", Assert.Single(provider.SchemaConnectionStrings));

        established.Dispose();

        Assert.True(tunnel.IsClosed);
    }

    [Theory]
    [InlineData(ConnectionFailureCategory.SshAuthentication)]
    [InlineData(ConnectionFailureCategory.SshHostUnreachable)]
    public async Task Test_SshFailure_ReportsTheSshCategoryWithoutTryingTheDatabase(ConnectionFailureCategory category)
    {
        var provider = new FakeProvider("PostgreSQL");
        var ssh = new FakeSshTransport
        {
            Failure = new SshTunnelException(category, "ssh failed", new SocketException((int)SocketError.ConnectionRefused))
        };

        var result = await Establisher(provider, ssh).TestAsync(Tunnelled());

        Assert.Equal(category, result.Category);
        Assert.Empty(provider.TestedConnectionStrings);
    }

    [Fact]
    public async Task Establish_SshFailure_ThrowsTheSshCategory()
    {
        var ssh = new FakeSshTransport { Failure = new SshTunnelException(ConnectionFailureCategory.SshAuthentication, "denied") };

        var error = await Assert.ThrowsAsync<SshTunnelException>(
            () => Establisher(new FakeProvider("PostgreSQL"), ssh).EstablishAsync(Tunnelled()));

        Assert.Equal(ConnectionFailureCategory.SshAuthentication, error.Category);
    }

    [Fact]
    public async Task Establish_CancelledAsTheTunnelOpens_ClosesTheTunnelAndHandsBackNothing()
    {
        using var cancellation = new CancellationTokenSource();
        var ssh = new FakeSshTransport { OnOpen = cancellation.Cancel };

        var establish = Establisher(new FakeProvider("PostgreSQL"), ssh).EstablishAsync(Tunnelled(), cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => establish);
        Assert.True(Assert.Single(ssh.Opened).IsClosed);
    }

    [Fact]
    public async Task SshNet_UnreachableBastion_ReportsSshHostUnreachable()
    {
        var provider = new FakeProvider("PostgreSQL");
        var connection = Tunnelled();
        connection.SshTunnel!.SshHost = "127.0.0.1";
        connection.SshTunnel.SshPort = ClosedPort();

        var result = await new ConnectionEstablisher(Databases(provider), new SshNetTransport()).TestAsync(connection);

        Assert.Equal(ConnectionFailureCategory.SshHostUnreachable, result.Category);
        Assert.Empty(provider.TestedConnectionStrings);
    }

    [Fact]
    public async Task SshNet_CancelledWhileTheBastionIsSilent_ClosesTheSession()
    {
        // A listener that accepts TCP but never sends an SSH banner, so the open stays in progress until cancelled.
        using var bastion = new TcpListener(IPAddress.Loopback, 0);
        bastion.Start();
        var request = new SshTunnelRequest(
            new SshTunnelConfig { Enabled = true, SshHost = "127.0.0.1", SshPort = ((IPEndPoint)bastion.LocalEndpoint).Port, SshUsername = "u", SshPassword = "p" },
            "db.internal", 6543, TimeSpan.FromSeconds(30), _ => true);
        using var cancellation = new CancellationTokenSource();

        var open = new SshNetTransport().OpenAsync(request, cancellation.Token);
        using var accepted = await bastion.AcceptSocketAsync().WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open.WaitAsync(TimeSpan.FromSeconds(10)));
        var buffer = new byte[256];
        int read;
        do
            read = await accepted.ReceiveAsync(buffer, SocketFlags.None).WaitAsync(TimeSpan.FromSeconds(10));
        while (read > 0);
    }

    [Fact]
    public void SshNet_RejectedCredentials_ClassifyAsSshAuthentication()
    {
        Assert.Equal(ConnectionFailureCategory.SshAuthentication, SshNetTransport.Classify(new SshAuthenticationException("Permission denied (password).")));
        Assert.Equal(ConnectionFailureCategory.SshAuthentication, SshNetTransport.Classify(new SshPassPhraseNullOrEmptyException("passphrase required")));
        Assert.Equal(ConnectionFailureCategory.SshHostUnreachable, SshNetTransport.Classify(new SocketException((int)SocketError.HostUnreachable)));
        Assert.Equal(ConnectionFailureCategory.SshHostUnreachable, SshNetTransport.Classify(new SshOperationTimeoutException()));
    }

    private static ConnectionEstablisher Establisher(FakeProvider provider, ISshTransport ssh) => new(Databases(provider), ssh);

    private static DatabaseService Databases(FakeProvider provider)
    {
        var databases = new DatabaseService();
        databases.RegisterProvider(provider);
        return databases;
    }

    private static ConnectionInfo Tunnelled() => new()
    {
        Name = "Behind bastion",
        Provider = "PostgreSQL",
        ConnectionString = Remote,
        SshTunnel = new SshTunnelConfig { Enabled = true, SshHost = "bastion.example", SshUsername = "jump", SshPassword = "secret" }
    };

    private static int ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class FakeSshTransport : ISshTransport
    {
        public int LocalPort { get; init; } = 40000;
        public Exception? Failure { get; init; }
        /// <summary>Runs as the tunnel opens, before it is handed back.</summary>
        public Action? OnOpen { get; init; }
        public List<SshTunnelRequest> Requests { get; } = [];
        public List<FakeTunnel> Opened { get; } = [];

        public Task<ISshTunnel> OpenAsync(SshTunnelRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Failure is not null)
                throw Failure;
            var tunnel = new FakeTunnel(LocalPort);
            Opened.Add(tunnel);
            OnOpen?.Invoke();
            return Task.FromResult<ISshTunnel>(tunnel);
        }
    }

    private sealed class FakeTunnel(int localPort) : ISshTunnel
    {
        public int LocalPort { get; } = localPort;
        public bool IsClosed { get; private set; }
        public void Dispose() => IsClosed = true;
    }
}
