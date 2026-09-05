using System.Globalization;
using System.IO;
using System.Net;
using System.Text.Json;

namespace ClaudeUsageTray.Core;

/// <summary>Read-only client for the public Claude status page (StatusPage v2). One
/// unauthenticated GET per refresh, no token, no cookies. Never throws; returns null on
/// timeout, network error, non-2xx, non-object root, or a missing/invalid status.indicator —
/// the caller then keeps its last-known-good state and backs off.</summary>
public static class PlatformStatusApi
{
    private static readonly string UserAgent =
        $"ClaudeUsageTray/{typeof(PlatformStatusApi).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}";

    public static async Task<PlatformStatus?> FetchAsync(HttpClient http, StatusSource source,
        DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source.SummaryUrl);
            request.Headers.UserAgent.ParseAdd(UserAgent);

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            if (!doc.RootElement.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Object)
                return null;
            if (!status.TryGetProperty("indicator", out var indicator) || indicator.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(indicator.GetString()))
                return null;

            // The banner text is the page's own wording and is shown verbatim; an empty banner
            // falls back to the indicator name at display time, not here.
            var description = status.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() ?? ""
                : "";

            var incidents = new List<PlatformIncident>();
            if (doc.RootElement.TryGetProperty("incidents", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in list.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object) continue;
                    // An incident with no name has nothing to show; the rest of the page survives.
                    if (NonEmptyString(entry, "name") is not { } name) continue;
                    incidents.Add(new PlatformIncident(
                        Name: name,
                        Status: NonEmptyString(entry, "status") ?? "unknown",
                        Impact: NonEmptyString(entry, "impact"),
                        Shortlink: NonEmptyString(entry, "shortlink"),
                        UpdatedAt: ReadTimestamp(entry, "updated_at"),
                        Components: ReadIncidentComponents(entry)));
                }
            }

            return new PlatformStatus(source.Id, now, indicator.GetString()!.Trim(), description,
                incidents, ReadComponents(doc.RootElement));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException
            or OperationCanceledException or IOException or JsonException)
        {
            return null;
        }
    }

    private static string? NonEmptyString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    /// <summary>ISO-8601 timestamp normalised to UTC; null when absent or unparseable — a bad
    /// timestamp must not drop the incident that carries it.</summary>
    private static DateTimeOffset? ReadTimestamp(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var r) && r.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(r.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed;
        }
        return null;
    }

    private static IReadOnlyList<string> ReadIncidentComponents(JsonElement entry)
    {
        if (!entry.TryGetProperty("components", out var comps) || comps.ValueKind != JsonValueKind.Array)
            return [];
        var names = new List<string>();
        foreach (var c in comps.EnumerateArray())
            if (c.ValueKind == JsonValueKind.Object && NonEmptyString(c, "name") is { } name)
                names.Add(name);
        return names;
    }

    /// <summary>Non-operational components only. status.openai.com sends no incidents at all, so this
    /// array is the only thing that can say what a disruption affects; an entry without a name or a
    /// status is dropped rather than shown as a blank row.</summary>
    private static IReadOnlyList<PlatformComponent> ReadComponents(JsonElement root)
    {
        if (!root.TryGetProperty("components", out var list) || list.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<PlatformComponent>();
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (NonEmptyString(entry, "name") is not { } name) continue;
            if (NonEmptyString(entry, "status") is not { } status) continue;
            if (status == "operational") continue;
            result.Add(new PlatformComponent(name, status));
        }
        return result;
    }
}