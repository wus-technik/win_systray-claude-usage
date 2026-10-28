using System.Text.Json;

namespace ClaudeUsageTray.Core;

/// <summary>
/// Parses `GET /backend-api/codex/usage` into the app's own <see cref="UsageSnapshot"/>, so every
/// severity, pace and drawing decision downstream is the one Claude's data already gets.
///
/// Three deliberate omissions. **Credits produce no row**: the block carries has_credits, unlimited
/// and a null balance — a capability flag, not a measurement — and CreditUsage needs a percentage,
/// so a synthesised zero would draw a green 0 % bar, which is exactly the placeholder the display
/// invariants forbid. **The windows map positionally**, primary → five-hour and secondary →
/// seven-day: limit_window_seconds is not consulted, because the app has two window slots with
/// fixed periods and dropping a window whose declared length changed would be worse than showing
/// it. **Nothing throws**: any shape that is not an object with at least one usable figure is null.
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
                five = ReadWindow(rate, "primary_window");
                seven = ReadWindow(rate, "secondary_window");
            }

            var scoped = ReadSpendControl(doc.RootElement);

            // No usable figure is not a reading: adopting it would reset the freshness clock and
            // make the display assert data it does not have.
            if (five is null && seven is null && scoped.Count == 0) return null;

            return new UsageSnapshot(fetchedAt, five, seven, scoped) { Source = UsageSource.Codex };
        }
        catch (JsonException) { return null; }
    }

    private static WindowUsage? ReadWindow(JsonElement rate, string name)
    {
        if (!rate.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object)
            return null;
        if (!window.TryGetProperty("used_percent", out var percent)
            || percent.ValueKind != JsonValueKind.Number
            || !TryPercent(percent, out var used)) return null;

        return new WindowUsage(used, ReadReset(window));
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
