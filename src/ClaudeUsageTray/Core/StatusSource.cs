namespace ClaudeUsageTray.Core;

/// <summary>One public status page the tray may watch. Everything that differs between pages is a
/// field here rather than a branch at the call site — <see cref="RaisesBadge"/> in particular, so
/// "an OpenAI outage never marks the tray icon" is a value a test can assert.</summary>
/// <param name="Id">The settings.json token. Lower-case ASCII.</param>
/// <param name="EnabledByDefault">Watched when settings carry no entry for this source.</param>
/// <param name="DefaultComponents">Watch filter used when settings carry no list for this source.
/// Empty means watch every component.</param>
public sealed record StatusSource(
    string Id,
    string DisplayName,
    string SummaryUrl,
    string PageUrl,
    string PageLabel,
    bool RaisesBadge,
    bool EnabledByDefault,
    IReadOnlyList<string> DefaultComponents);

/// <summary>A source paired with its current state, as the popup and the tooltip consume it, so
/// neither reaches back into <see cref="Settings"/>.</summary>
public sealed record SourceView(StatusSource Source, PlatformStatus? Status, IReadOnlyList<string> Filter);

/// <summary>The curated set of watchable pages. No user-supplied URLs: the app only ever fetches a
/// host it ships, and only payload shapes that were verified by hand. Exactly these two are
/// supported and tested; the registry is generic so the badge rule and the watch filter are data,
/// not because a third source is planned.</summary>
public static class StatusSourceRegistry
{
    public static readonly StatusSource Claude = new(
        Id: "claude",
        DisplayName: "Claude",
        SummaryUrl: "https://status.claude.com/api/v2/summary.json",
        PageUrl: "https://status.claude.com",
        PageLabel: "status.claude.com",
        RaisesBadge: true,
        EnabledByDefault: true,
        // All six of Claude's components matter to this app; the field exists for symmetry.
        DefaultComponents: []);

    public static readonly StatusSource OpenAi = new(
        Id: "openai",
        DisplayName: "OpenAI",
        SummaryUrl: "https://status.openai.com/api/v2/summary.json",
        PageUrl: "https://status.openai.com",
        PageLabel: "status.openai.com",
        RaisesBadge: false,
        EnabledByDefault: false,
        // 25 components span products a Codex user does not use (Sora, Ads API, FedRAMP, …).
        // "codex" alone matches Codex API, Codex Web, and Codex in ChatGPT Desktop.
        DefaultComponents: ["codex", "responses", "login", "vs code extension"]);

    public static IReadOnlyList<StatusSource> All { get; } = [Claude, OpenAi];

    /// <summary>The registry's lookup API for callers holding a settings id (case-insensitive; null
    /// for unknown ids).</summary>
    public static StatusSource? ById(string? id)
        => id is null ? null : All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
}
