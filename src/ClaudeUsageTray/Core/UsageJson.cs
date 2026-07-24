using System.Globalization;
using System.Text.Json;

namespace ClaudeUsageTray.Core;

/// <summary>Shared parser for one usage window ({ "utilization": number, "resets_at": iso }) —
/// used by both the .claude.json cache reader and the usage-API client. Utilization is read as a
/// double and rounded: the cache stores integers (1, 13) but the live API returns decimals
/// (11.0, 53.6), and Int32 parsing rejects any fractional form — silently nulling live windows.</summary>
internal static class UsageJson
{
    internal static WindowUsage? ReadWindow(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        if (!w.TryGetProperty("utilization", out var p) || p.ValueKind != JsonValueKind.Number
            || !p.TryGetDouble(out var utilization)) return null;
        int percent = (int)Math.Round(utilization, MidpointRounding.AwayFromZero);

        DateTimeOffset? resetsAt = null;
        if (w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(r.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            resetsAt = parsed;
        }
        return new WindowUsage(percent, resetsAt);
    }
}
