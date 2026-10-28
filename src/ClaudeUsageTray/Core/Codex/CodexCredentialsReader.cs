using System.Text.Json;
using System.Security;

namespace ClaudeUsageTray.Core;

/// <summary>Codex's stored token, as far as the tray needs it. ExpiresAt comes from the token's own
/// exp claim: an expiry the app can see is a known no-data reason, which beats discovering it as a
/// 401 after spending a request from an unmeasured budget.</summary>
public sealed record CodexAuth(string AccessToken, string? AccountId, DateTimeOffset? ExpiresAt);

/// <summary>
/// Read-only extraction of the Codex CLI's OAuth access token. This app NEVER writes auth.json,
/// never refreshes tokens, and never logs or persists token material — the same invariant the
/// Claude credentials path carries.
/// </summary>
public static class CodexCredentialsReader
{
    /// <summary>Margin below which a token is treated as unusable, because Codex is about to
    /// refresh it. Same five minutes as the Claude path.</summary>
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(5);

    /// <summary>tokens.access_token when present, non-empty, and valid past the margin; else null.
    /// Never throws. A token whose expiry cannot be read is unusable rather than assumed good: the
    /// alternative is spending requests on a token that will only ever answer 401.</summary>
    public static CodexAuth? TryRead(string path, DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length > 32 * 1024 * 1024) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(stream);

            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("tokens", out var tokens)
                || tokens.ValueKind != JsonValueKind.Object) return null;
            if (!tokens.TryGetProperty("access_token", out var tokenElement)
                || tokenElement.ValueKind != JsonValueKind.String) return null;
            var token = tokenElement.GetString();
            if (string.IsNullOrEmpty(token)) return null;

            var expires = ExpiryOf(token);
            if (expires is null || expires <= now + ExpiryMargin) return null;

            string? account = tokens.TryGetProperty("account_id", out var id)
                              && id.ValueKind == JsonValueKind.String ? id.GetString() : null;

            return new CodexAuth(token, account, expires);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException
            or ArgumentException or SecurityException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Existence plus the same validation <see cref="TryRead"/> applies. The token itself is
    /// not returned or retained.</summary>
    public static CredentialStatus Status(string path, DateTimeOffset now)
    {
        bool exists;
        try { exists = File.Exists(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        { return CredentialStatus.Unusable; }
        if (!exists) return CredentialStatus.Missing;
        return TryRead(path, now) is null ? CredentialStatus.Unusable : CredentialStatus.Valid;
    }

    /// <summary>The exp claim of a three-segment JWT, or null for anything else. Decoding only — no
    /// signature check: the token is not being trusted here, merely asked when it stops working.</summary>
    private static DateTimeOffset? ExpiryOf(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return null;

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));

            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("exp", out var exp)
                || exp.ValueKind != JsonValueKind.Number
                || !exp.TryGetInt64(out var seconds)) return null;
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
