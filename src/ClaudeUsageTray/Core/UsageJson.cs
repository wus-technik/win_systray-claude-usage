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

    /// <summary>Renderable scoped weekly limits from limits[], deduped by label and ordered
    /// active-first then by descending percent. Empty when limits[] is absent or unusable.
    /// Entries needing a label they cannot supply are skipped: a bar captioned with nothing is
    /// worse than a missing bar.</summary>
    internal static IReadOnlyList<ScopedLimit> ReadScopedLimits(JsonElement parent)
    {
        if (!parent.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Array)
            return [];

        // Insertion-ordered dedup: the dictionary merges collisions, the list keeps ties stable.
        var merged = new Dictionary<string, ScopedLimit>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var entry in limits.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (NonEmptyString(entry, "group") is not "weekly") continue;
            if (!entry.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.Object)
                continue;

            string? modelName = null, modelId = null;
            if (scope.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object)
            {
                modelName = NonEmptyString(model, "display_name");
                modelId = NonEmptyString(model, "id");
            }
            var surface = NonEmptyString(scope, "surface")?.Replace('_', ' ');

            // display_name first: it is the field observed populated, while id is observed null.
            var label = (modelName ?? modelId, surface) switch
            {
                ({ } m, { } s) => $"{m} ({s})",
                ({ } m, null) => m,
                (null, { } s) => s,
                _ => null,
            };
            if (label is null) continue;
            if (ReadRoundedPercent(entry, "percent") is not { } percent) continue;

            var candidate = new ScopedLimit(label, modelId, percent, ReadResetsAt(entry),
                IsActive: entry.TryGetProperty("is_active", out var a) && a.ValueKind == JsonValueKind.True);

            if (merged.TryGetValue(label, out var existing)) merged[label] = Merge(existing, candidate);
            else { merged[label] = candidate; order.Add(label); }
        }

        return order.Select(k => merged[k])
            .OrderByDescending(l => l.IsActive)
            .ThenByDescending(l => l.Percent)
            .ThenBy(l => l.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Keeps the higher percent so a dedup never makes usage look lower than it is; ties
    /// keep the first entry. ModelId and IsActive survive from either side — losing an active flag
    /// to a dedup would forfeit the row's exemption from the popup's row cap.</summary>
    private static ScopedLimit Merge(ScopedLimit first, ScopedLimit second)
    {
        var winner = second.Percent > first.Percent ? second : first;
        return winner with
        {
            ModelId = winner.ModelId ?? first.ModelId ?? second.ModelId,
            IsActive = first.IsActive || second.IsActive,
        };
    }
}
