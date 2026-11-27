using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace DbConnect.Services;

public record UpdateInfo(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("releaseUrl")] string ReleaseUrl);

public class UpdateCheckService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// GETs <paramref name="updateUrl"/> and returns an <see cref="UpdateInfo"/> if the response
    /// is valid JSON with a version field. Returns null on any error or if URL is empty.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(string updateUrl)
    {
        if (string.IsNullOrWhiteSpace(updateUrl)) return null;
        try
        {
            var json = await _http.GetStringAsync(updateUrl);
            return JsonSerializer.Deserialize<UpdateInfo>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the current assembly's informational version (e.g. "2.0.0" or "0.0.1-dev").
    /// Strips any build metadata suffix (everything after '+').
    /// </summary>
    /// <summary>The version without a pre-release or build suffix ("1.2.3-beta+7" → "1.2.3").</summary>
    public static string StripPreRelease(string version)
    {
        var idx = version.IndexOfAny(['-', '+']);
        return idx >= 0 ? version[..idx] : version;
    }

    public static string CurrentVersion()
    {
        var raw = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.1-dev";
        var plusIdx = raw.IndexOf('+');
        return plusIdx >= 0 ? raw[..plusIdx] : raw;
    }
}
