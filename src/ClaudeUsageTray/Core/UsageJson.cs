using System.Globalization;
using System.Text.Json;

namespace ClaudeUsageTray.Core;

/// <summary>Shared parsers for the usage payload — used by both the .claude.json cache reader
/// and the usage-API client, which see the same fields at different nesting levels.</summary>
internal static class UsageJson
{
    /// <summary>Reads a percentage as a double and rounds it: the cache stores integers (1, 13)
    /// but the live API returns decimals (11.0, 53.6), and Int32 parsing rejects any fractional
    /// form — silently nulling live windows.</summary>
    internal static int? ReadRoundedPercent(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Number
            || !p.TryGetDouble(out var value)) return null;
        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    /// <summary>Reads an ISO-8601 "resets_at", normalised to UTC. Null when absent or unparseable.</summary>
    internal static DateTimeOffset? ReadResetsAt(JsonElement element)
    {
        if (element.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(r.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed;
        }
        return null;
    }

    /// <summary>One usage window ({ "utilization": number, "resets_at": iso }).</summary>
    internal static WindowUsage? ReadWindow(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        if (ReadRoundedPercent(w, "utilization") is not { } percent) return null;
        return new WindowUsage(percent, ReadResetsAt(w));
    }

    /// <summary>A trimmed non-empty string property, or null.</summary>
    internal static string? NonEmptyString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
