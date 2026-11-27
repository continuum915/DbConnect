namespace DbConnect.Models;

public class SshTunnelConfig
{
    public bool Enabled { get; set; }
    public string SshHost { get; set; } = string.Empty;
    public int SshPort { get; set; } = 22;
    public string SshUsername { get; set; } = string.Empty;

    /// <summary>"Password" or "PrivateKey"</summary>
    public string AuthMethod { get; set; } = "Password";

    /// <summary>Not persisted to disk — re-entered each session until P2-032.</summary>
    public string SshPassword { get; set; } = string.Empty;

    public string PrivateKeyPath { get; set; } = string.Empty;

    /// <summary>Not persisted to disk — re-entered each session until P2-032.</summary>
    public string PrivateKeyPassphrase { get; set; } = string.Empty;

    /// <summary>Local port to bind; 0 = pick a free port automatically.</summary>
    public int LocalPort { get; set; } = 0;
}
