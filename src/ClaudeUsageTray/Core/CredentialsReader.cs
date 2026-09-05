using System.Text.Json;

namespace ClaudeUsageTray.Core;

/// <summary>
/// Read-only extraction of Claude Code's OAuth access token. This app NEVER writes the
/// credentials file, never refreshes tokens, and never logs or persists token material.
/// </summary>
public static class CredentialsReader
{
    /// <summary>Margin below which a token is treated as unusable (Claude Code will refresh it).</summary>
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(5);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    /// <summary>claudeAiOauth.accessToken when present, non-empty, and valid past the margin; else null. Never throws.</summary>
    public static string? TryReadAccessToken(string path, DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length > 32 * 1024 * 1024) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(stream);

            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                || oauth.ValueKind != JsonValueKind.Object) return null;
            if (!oauth.TryGetProperty("accessToken", out var tokenElement)
                || tokenElement.ValueKind != JsonValueKind.String) return null;
            var token = tokenElement.GetString();
            if (string.IsNullOrEmpty(token)) return null;
            if (!oauth.TryGetProperty("expiresAt", out var expires)
                || expires.ValueKind != JsonValueKind.Number
                || !expires.TryGetInt64(out var expiresAtMs)) return null;
            if (DateTimeOffset.FromUnixTimeMilliseconds(expiresAtMs) <= now + ExpiryMargin) return null;
            return token;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException
            or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Existence plus the same validation <see cref="TryReadAccessToken"/> applies. The token
    /// itself is not returned or retained.</summary>
    public static CredentialStatus Status(string path, DateTimeOffset now)
    {
        bool exists;
        try { exists = File.Exists(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        { return CredentialStatus.Unusable; }
        if (!exists) return CredentialStatus.Missing;
        return TryReadAccessToken(path, now) is null ? CredentialStatus.Unusable : CredentialStatus.Valid;
    }
}
