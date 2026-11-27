using DbConnect.Models;
using DbConnect.Services;
using FakeProvider = DbConnect.Tests.ConnectionEstablisherTests.FakeProvider;

namespace DbConnect.Tests;

/// <summary>External SSH/database boundaries; the production establisher and file store run unchanged.</summary>
internal sealed class SshTrustTestContext : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), $"rain-ssh-trust-{Guid.NewGuid():N}");
    public KnownHostsStore Store { get; }
    public FakeProvider Provider { get; } = new("PostgreSQL");
    public DatabaseService Databases { get; } = new();
    public PresentingTransport Transport { get; } = new();
    public ConnectionEstablisher Establisher { get; }
    public ConnectionInfo Connection { get; } = new()
    {
        Provider = "PostgreSQL",
        ConnectionString = "Host=db.internal;Port=5432;Database=app;Username=u;Password=p",
        SshTunnel = new SshTunnelConfig { Enabled = true, SshHost = "bastion.example", SshPort = 22, SshUsername = "u" }
    };

    public SshTrustTestContext()
    {
        Directory.CreateDirectory(DirectoryPath);
        Store = new KnownHostsStore(DirectoryPath);
        Databases.RegisterProvider(Provider);
        Establisher = new ConnectionEstablisher(Databases, Transport, Store);
    }

    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);

    internal sealed class PresentingTransport : ISshTransport
    {
        public SshHostKey Key { get; set; } = new("bastion.example", 22, "ssh-ed25519", "SHA256:presented-key");
        public int ActiveSessions { get; private set; }
        public int ActiveForwards { get; private set; }

        public Task<ISshTunnel> OpenAsync(SshTunnelRequest request, CancellationToken cancellationToken)
        {
            ActiveSessions++;
            try
            {
                if (!request.AcceptHostKey(Key))
                    throw new SshTunnelException(ConnectionFailureCategory.SshHostKeyRejected, "Host key rejected");
                cancellationToken.ThrowIfCancellationRequested();
                ActiveForwards++;
                return Task.FromResult<ISshTunnel>(new Tunnel(this));
            }
            catch
            {
                ActiveSessions--;
                throw;
            }
        }

        private sealed class Tunnel(PresentingTransport transport) : ISshTunnel
        {
            private bool _disposed;
            public int LocalPort => 40123;
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                transport.ActiveForwards--;
                transport.ActiveSessions--;
            }
        }
    }
}
