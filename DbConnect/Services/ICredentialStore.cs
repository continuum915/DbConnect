using System.Threading.Tasks;

namespace DbConnect.Services;

public interface ICredentialStore
{
    /// <summary>Store a secret under the given key, replacing any existing value.</summary>
    Task SetAsync(string key, string secret);

    /// <summary>Retrieve a secret by key; returns null if not found.</summary>
    Task<string?> GetAsync(string key);

    /// <summary>Delete a secret. No-op if the key does not exist.</summary>
    Task DeleteAsync(string key);
}
