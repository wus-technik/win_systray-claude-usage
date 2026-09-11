namespace ClaudeUsageTray.Core;

/// <summary>One clickable component name inside the hint caption: the name itself, and where it sits
/// in the rendered text.</summary>
public readonly record struct ComponentHintLink(string Name, int Start, int Length);

/// <summary>The caption listing a page's own component names, with the span of each name — the
/// dialog renders it as links that fill the watch filter, so the offsets have to come from building
/// the string rather than from searching it afterwards.</summary>
public sealed record ComponentHintText(string Text, IReadOnlyList<ComponentHintLink> Links);

public static class ComponentHint
{
    private const string Prefix = "Page lists: ";

    /// <summary>The caption for one page's names. Never a prefill: the box still shows exactly what
    /// is stored, so "blank = all" stays literally true until the user clicks something.</summary>
    public static ComponentHintText For(IReadOnlyList<string>? names)
    {
        if (names is null || names.Count == 0) return new(Prefix + "not fetched yet", []);

        var text = new System.Text.StringBuilder(Prefix);
        var links = new List<ComponentHintLink>(names.Count);
        foreach (var name in names)
        {
            if (links.Count > 0) text.Append(", ");
            links.Add(new ComponentHintLink(name, text.Length, name.Length));
            text.Append(name);
        }
        return new(text.ToString(), links);
    }
}
