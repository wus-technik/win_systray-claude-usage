using System.Globalization;
using System.Text.Json;

namespace ClaudeUsageTray.Core;

/// <summary>Shared parser for one usage window ({ "utilization": int, "resets_at": iso }) —
/// used by both the .claude.json cache reader and the usage-API client.</summary>
internal static class UsageJson
{
    internal static WindowUsage? ReadWindow(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        if (!w.TryGetProperty("utilization", out var p) || p.ValueKind != JsonValueKind.Number
            || !p.TryGetInt32(out var percent)) return null;

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
