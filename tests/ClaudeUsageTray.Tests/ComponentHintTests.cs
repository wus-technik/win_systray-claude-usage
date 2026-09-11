using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>The caption that lists a page's own component names, and where each name sits inside it
/// — the dialog turns those spans into links, so an offset that is off by one clicks the wrong name.
/// </summary>
public class ComponentHintTests
{
    [Fact]
    public void EveryNameGetsASpanThatCoversExactlyThatName()
    {
        var hint = ComponentHint.For(["claude.ai", "Claude Code", "Claude Cowork"]);

        Assert.Equal("Page lists: claude.ai, Claude Code, Claude Cowork", hint.Text);
        Assert.Equal(
            new[] { "claude.ai", "Claude Code", "Claude Cowork" },
            hint.Links.Select(link => hint.Text.Substring(link.Start, link.Length)));
        Assert.Equal(
            new[] { "claude.ai", "Claude Code", "Claude Cowork" },
            hint.Links.Select(link => link.Name));
    }

    /// <summary>A name that is a prefix of a later one is why the spans are counted while the text is
    /// built rather than searched for afterwards: IndexOf("Claude") would find the same occurrence
    /// twice and both links would insert the short name.</summary>
    [Fact]
    public void ANameThatRepeatsInsideALaterNameStillGetsItsOwnSpan()
    {
        var hint = ComponentHint.For(["Claude", "Claude Code"]);

        Assert.Equal("Page lists: Claude, Claude Code", hint.Text);
        Assert.Equal(
            new[] { "Claude", "Claude Code" },
            hint.Links.Select(link => hint.Text.Substring(link.Start, link.Length)));
    }

    /// <summary>Nothing fetched yet: the caption still says so, and there is nothing to click.</summary>
    [Fact]
    public void NoNamesMeansTheFallbackTextAndNoLinks()
    {
        Assert.Equal("Page lists: not fetched yet", ComponentHint.For([]).Text);
        Assert.Empty(ComponentHint.For([]).Links);
        Assert.Equal("Page lists: not fetched yet", ComponentHint.For(null).Text);
        Assert.Empty(ComponentHint.For(null).Links);
    }
}
