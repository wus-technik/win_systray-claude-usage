using System.Text.Json;

namespace ClaudeUsageTray.Core;

public static class UsageCacheReader
{
    /// <summary>
    /// Read-only parse of .claude.json → cachedUsageUtilization. Returns null when the file,
    /// key, or fetchedAtMs is missing or the JSON is malformed. Never throws for IO/JSON errors.
    /// </summary>
    public static UsageSnapshot? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            // FileShare.ReadWrite: Claude Code may be rewriting the file while we read.
            // 32 MiB sanity guard only: .claude.json accumulates project history and can
            // legitimately reach several MiB — a small cap would silently disable the app.
            var info = new FileInfo(path);
            if (info.Length > 32 * 1024 * 1024) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(stream);

            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("cachedUsageUtilization", out var cached)
                || cached.ValueKind != JsonValueKind.Object) return null;
            if (!cached.TryGetProperty("fetchedAtMs", out var fetched)
                || fetched.ValueKind != JsonValueKind.Number) return null;

            var fetchedAt = DateTimeOffset.FromUnixTimeMilliseconds(fetched.GetInt64());
            WindowUsage? five = null, seven = null;
            IReadOnlyList<ScopedLimit> scoped = [];
            CreditUsage? credits = null;
            if (cached.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                five = UsageJson.ReadWindow(u, "five_hour");
                seven = UsageJson.ReadWindow(u, "seven_day");
                scoped = UsageJson.ReadScopedLimits(u);
                credits = UsageJson.ReadCredits(u);
            }
            return new UsageSnapshot(fetchedAt, five, seven, scoped, credits);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException
            or FormatException or OverflowException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private const string CachedUsageKeyNeedle = "\"cachedUsageUtilization\"";
    private const int StatusChunkSize = 64 * 1024;

    /// <summary>Why <see cref="TryRead"/> may have returned null, for the no-data message. A guarded
    /// string search rather than a parse: the file is Claude Code's and can be mid-rewrite.
    /// Scanned in 64 KiB chunks rather than read into one string — .claude.json can be many MiB and
    /// this runs on the UI thread on every 500 ms watcher debounce and 30 s recovery tick.</summary>
    public static ConfigStatus Status(string path)
    {
        try
        {
            if (!File.Exists(path)) return ConfigStatus.Missing;
            var info = new FileInfo(path);
            if (info.Length > 32 * 1024 * 1024) return ConfigStatus.Unreadable;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            var buffer = new char[StatusChunkSize];
            // Carries the tail of the previous chunk (needle length - 1 chars) in front of the next
            // one, so a match straddling a chunk boundary is still found.
            var carry = string.Empty;
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                var window = carry + new string(buffer, 0, read);
                if (window.Contains(CachedUsageKeyNeedle, StringComparison.Ordinal))
                    return ConfigStatus.Unreadable;
                var keep = CachedUsageKeyNeedle.Length - 1;
                carry = window.Length > keep ? window[^keep..] : window;
            }
            return ConfigStatus.NoUsageKey;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return ConfigStatus.Unreadable;
        }
    }
}
