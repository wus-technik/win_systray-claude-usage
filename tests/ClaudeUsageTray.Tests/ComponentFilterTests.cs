using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class ComponentFilterTests
{
    [Fact]
    public void Parse_TrimsDropsEmptiesAndDeduplicates()
    {
        Assert.Equal(["codex", "login"], ComponentFilter.Parse("  codex , , login ,CODEX,   "));
    }

    [Fact]
    public void Parse_OfNothing_IsTheEmptyFilter()
    {
        Assert.Empty(ComponentFilter.Parse(null));
        Assert.Empty(ComponentFilter.Parse("   ,  , "));
    }

    [Fact]
    public void EmptyFilter_MatchesEverything()
    {
        Assert.True(ComponentFilter.Matches("Sora", []));
    }

    [Fact]
    public void Matches_IsCaseInsensitiveSubstring()
    {
        IReadOnlyList<string> filter = ["codex"];
        Assert.True(ComponentFilter.Matches("Codex API", filter));
        Assert.True(ComponentFilter.Matches("Codex Web", filter));
        Assert.True(ComponentFilter.Matches("Codex in ChatGPT Desktop", filter));
        Assert.False(ComponentFilter.Matches("Sora", filter));
    }

    [Fact]
    public void Matches_AnyToken_IsEnough()
    {
        Assert.True(ComponentFilter.Matches("Login", ["codex", "login"]));
    }

    [Fact]
    public void Format_RoundTripsThroughParse()
    {
        var filter = ComponentFilter.Parse("codex, responses");
        Assert.Equal("codex, responses", ComponentFilter.Format(filter));
        Assert.Equal(filter, ComponentFilter.Parse(ComponentFilter.Format(filter)));
    }
}
