using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DbConnect.Services;

/// <summary>
/// Removes credentials and other high-risk values before diagnostics are persisted.
/// This is intentionally conservative: callers should avoid putting SQL/data in
/// diagnostics, and this class is a last line of defence rather than a policy engine.
/// </summary>
public static partial class DiagnosticRedactor
{
    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "pwd", "secret", "token", "access_token", "access-token",
        "apikey", "api_key", "api-key", "client_secret", "client-secret",
        "private_key", "private-key", "credential", "credentials", "passphrase",
        "connectionstring", "connection_string", "connection-string"
    };

    /// <summary>Redacts common credential formats, URI credentials, bearer tokens, and private-key blocks.</summary>
    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;

        var redacted = PrivateKeyRegex().Replace(value, "[REDACTED_PRIVATE_KEY]");
        redacted = BearerTokenRegex().Replace(redacted, "$1[REDACTED]");
        redacted = UriCredentialRegex().Replace(redacted, "://[REDACTED]@");
        redacted = ConnectionStringRegex().Replace(redacted, "[CONNECTION_STRING_REDACTED]");
        redacted = SensitiveValueRegex().Replace(redacted, match =>
            $"{match.Groups["key"].Value}{match.Groups["separator"].Value}[REDACTED]");
        return redacted;
    }

    /// <summary>
    /// Produces a display-safe identity. Connection-string-shaped input is
    /// replaced wholesale because even non-secret fields can reveal topology.
    /// </summary>
    public static string SanitizeConnectionIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        if (ConnectionStringShapeRegex().IsMatch(value))
            return "[connection string redacted]";

        return Redact(value);
    }

    /// <summary>
    /// Converts arbitrary parameter values to safe strings. Values whose names
    /// are marked sensitive are replaced even when they are not formatted as key/value text.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> RedactParameters(
        IReadOnlyDictionary<string, object?> parameters,
        ISet<string>? sensitiveParameterNames = null)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in parameters)
        {
            result[pair.Key] = IsSensitiveKey(pair.Key) ||
                               (sensitiveParameterNames?.Contains(pair.Key) == true)
                ? "[REDACTED]"
                : Redact(pair.Value?.ToString());
        }

        return result;
    }

    public static bool IsSensitiveKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var normalized = key.Trim().Replace('-', '_');
        return SensitiveKeys.Contains(normalized) ||
               normalized.Contains("token", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("password", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith("_token", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith("_secret", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith("_password", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"-----BEGIN [^-]*PRIVATE KEY-----[\s\S]*?-----END [^-]*PRIVATE KEY-----", RegexOptions.IgnoreCase)]
    private static partial Regex PrivateKeyRegex();

    [GeneratedRegex(@"(Bearer\s+)[^\s,;]+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"://[^\s/@:]+:[^\s/@]+@", RegexOptions.IgnoreCase)]
    private static partial Regex UriCredentialRegex();

    [GeneratedRegex(@"(?<key>\b(?:password|pwd|secret|token|access[_-]?token|api[_-]?key|client[_-]?secret|private[_-]?key|credential(?:s|store)?|passphrase|connection[_-]?string)\b)\s*(?<separator>[:=])\s*(?:""[^""]*""|'[^']*'|[^;\s,}\]]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveValueRegex();

    [GeneratedRegex(@"\b(?:server|host|data\s+source|database|initial\s+catalog|user\s+id|uid)\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionStringShapeRegex();

    [GeneratedRegex(@"\b(?:server|host|data\s+source|initial\s+catalog)\s*=\s*[^;\r\n]*(?:;\s*[^=\r\n;]+=\s*[^;\r\n]*)*", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionStringRegex();
}
