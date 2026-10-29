using System.Text.Json;

namespace ClaudeUsageTray.Core;

/// <summary>
/// Parses `GET /backend-api/wham/usage` into the app's own <see cref="UsageSnapshot"/>, so every
/// severity, pace and drawing decision downstream is the one Claude's data already gets.
///
/// Three deliberate omissions. **Credits produce no row**: the block carries has_credits, unlimited
/// and a null balance — a capability flag, not a measurement — and CreditUsage needs a percentage,
/// so a synthesised zero would draw a green 0 % bar, which is exactly the placeholder the display
/// invariants forbid. **Windows map by duration**: primary can be a weekly-only limit. Only when
/// limit_window_seconds is absent do primary and secondary fall back to five-hour and seven-day.
/// Unsupported or malformed durations cannot use the app's fixed-period slots. **Nothing throws**:
/// any shape that is not an object with at least one usable figure is null.
/// </summary>
public static class CodexUsageJson
{
    /// <summary>A fixed string this app supplies, not a payload label — which is what makes it safe
    /// to name in fetch.log, unlike a scoped limit's account-specific model name.</summary>
    public const string SpendControlLabel = "Spend control";

    public static UsageSnapshot? Parse(string json, DateTimeOffset fetchedAt)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            WindowUsage? five = null, seven = null;
            if (doc.RootElement.TryGetProperty("rate_limit", out var rate)
                && rate.ValueKind == JsonValueKind.Object)
            {
                ReadWindow(rate, "primary_window", 18000, ref five, ref seven);
                ReadWindow(rate, "secondary_window", 604800, ref five, ref seven);
            }

            var scoped = ReadSpendControl(doc.RootElement);

            // No usable figure is not a reading: adopting it would reset the freshness clock and
            // make the display assert data it does not have.
            if (five is null && seven is null && scoped.Count == 0) return null;

            return new UsageSnapshot(fetchedAt, five, seven, scoped) { Source = UsageSource.Codex };
        }
        catch (JsonException) { return null; }
    }

    private static void ReadWindow(JsonElement rate, string name, int fallbackSeconds,
        ref WindowUsage? five, ref WindowUsage? seven)
    {
        if (!rate.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object)
            return;

        var seconds = fallbackSeconds;
        if (window.TryGetProperty("limit_window_seconds", out var duration)
            && (duration.ValueKind != JsonValueKind.Number || !duration.TryGetInt32(out seconds)))
            return;

        if (!window.TryGetProperty("used_percent", out var percent)
            || percent.ValueKind != JsonValueKind.Number
            || !TryPercent(percent, out var used)) return;

        // Keep the first usable reading if a malformed payload repeats the same period.
        if (seconds == 18000) five ??= new WindowUsage(used, ReadReset(window));
        else if (seconds == 604800) seven ??= new WindowUsage(used, ReadReset(window));
    }

    /// <summary>The workspace spend cap, as a scoped limit with its own period. Its reset is a
    /// calendar-month boundary, so Monthly rather than the default Weekly; IsActive is true because
    /// the payload only sends this block when the cap applies. The money fields — limit, used,
    /// remaining — are deliberately not read: the percentage is the whole of what this app shows,
    /// and amounts have no path into the display or the log.</summary>
    private static IReadOnlyList<ScopedLimit> ReadSpendControl(JsonElement root)
    {
        if (!root.TryGetProperty("spend_control", out var spend)
            || spend.ValueKind != JsonValueKind.Object) return [];
        if (!spend.TryGetProperty("individual_limit", out var limit)
            || limit.ValueKind != JsonValueKind.Object) return [];
        if (!limit.TryGetProperty("used_percent", out var percent)
            || percent.ValueKind != JsonValueKind.Number
            || !TryPercent(percent, out var used)) return [];

        return [new ScopedLimit(SpendControlLabel, null, used, ReadReset(limit),
            IsActive: true) { Period = ScopedPeriod.Monthly }];
    }

    /// <summary>Reject a figure the snapshot's integer percentage cannot represent. Without this
    /// check an overflowing double-to-int conversion can turn an absurd value into a negative bar.</summary>
    private static bool TryPercent(JsonElement element, out int percent)
    {
        percent = 0;
        if (!element.TryGetDouble(out var value) || !double.IsFinite(value)) return false;
        var rounded = Math.Round(value);
        if (rounded < int.MinValue || rounded > int.MaxValue) return false;
        percent = (int)rounded;
        return true;
    }

    /// <summary>reset_at is unix seconds, unlike the Claude payload's ISO-8601 strings. An absurd
    /// value drops the reset and keeps the percentage, rather than taking the read path down.</summary>
    private static DateTimeOffset? ReadReset(JsonElement element)
    {
        if (!element.TryGetProperty("reset_at", out var reset)
            || reset.ValueKind != JsonValueKind.Number
            || !reset.TryGetInt64(out var seconds)) return null;

        try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
