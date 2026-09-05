namespace ClaudeUsageTray.Core;

/// <summary>The watch filter: which of a page's components the user cares about. Substring rather
/// than exact names, because these pages rename components (`Codex in ChatGPT Desktop` appeared in
/// 2026-03) and one token should keep matching. Ordinal throughout — these are US-English product
/// names, and a Turkish locale must not change what "login" matches.</summary>
public static class ComponentFilter
{
    /// <summary>Trimmed, non-empty, de-duplicated tokens. A list that normalizes to nothing is the
    /// empty filter, which watches everything.</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? tokens)
    {
        if (tokens is null) return [];
        var result = new List<string>();
        foreach (var raw in tokens)
        {
            var token = raw?.Trim();
            if (string.IsNullOrEmpty(token)) continue;
            if (!result.Contains(token, StringComparer.OrdinalIgnoreCase)) result.Add(token);
        }
        return result;
    }

    /// <summary>The dialog's comma-separated text as a filter.</summary>
    public static IReadOnlyList<string> Parse(string? text) => Normalize(text?.Split(','));

    /// <summary>The filter as dialog text.</summary>
    public static string Format(IReadOnlyList<string> filter) => string.Join(", ", filter);

    /// <summary>Whether this component name is watched. An empty filter watches everything.</summary>
    public static bool Matches(string name, IReadOnlyList<string> filter)
    {
        if (filter.Count == 0) return true;
        foreach (var token in filter)
            if (name.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
