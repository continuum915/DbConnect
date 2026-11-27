using DbConnect.Services;

namespace DbConnect.Tests;

public sealed class EncryptedFileStoreTests
{
    [Fact]
    public async Task DecryptsSecretEncryptedWithLegacyKeyDerivation()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rain-legacy-vault-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            // Generated before migration with Rfc2898DeriveBytes(string, byte[], int, SHA256),
            // identity rain-legacy-machine\0rain-legacy-user, the v1 salt, 100,000 iterations,
            // 32 key bytes and nonce bytes 0..11. Keep the ciphertext literal as the oracle.
            await File.WriteAllTextAsync(Path.Combine(directory, "vault.json"),
                "{\"legacy\":\"AAECAwQFBgcICQoLe5ZbTn/F+DNQD4vykCkZPy/fg6RiPD7SUlAQKqXJ5kx9Ejpn5qPX3ew0i90aTTw=\"}");
            var store = new EncryptedFileStore(directory, "rain-legacy-machine\0rain-legacy-user");

            Assert.Equal("legacy vault secret: café 🔐", await store.GetAsync("legacy"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
