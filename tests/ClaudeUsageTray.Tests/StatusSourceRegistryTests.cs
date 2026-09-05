using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class StatusSourceRegistryTests
{
    [Fact]
    public void Registry_IsClaudeThenOpenAi()
    {
        Assert.Equal(["claude", "openai"], StatusSourceRegistry.All.Select(s => s.Id));
    }

    [Fact]
    public void OnlyClaude_RaisesTheBadge()
    {
        Assert.True(StatusSourceRegistry.Claude.RaisesBadge);
        Assert.False(StatusSourceRegistry.OpenAi.RaisesBadge);
    }

    [Fact]
    public void OnlyClaude_IsEnabledByDefault()
    {
        Assert.True(StatusSourceRegistry.Claude.EnabledByDefault);
        Assert.False(StatusSourceRegistry.OpenAi.EnabledByDefault);
    }

    [Fact]
    public void Endpoints_AreTheVerifiedSummaryUrls()
    {
        Assert.Equal("https://status.claude.com/api/v2/summary.json", StatusSourceRegistry.Claude.SummaryUrl);
        Assert.Equal("https://status.openai.com/api/v2/summary.json", StatusSourceRegistry.OpenAi.SummaryUrl);
    }

    [Fact]
    public void ClaudeWatchesEverything_OpenAiDefaultsToTheCodexSet()
    {
        Assert.Empty(StatusSourceRegistry.Claude.DefaultComponents);
        Assert.Equal(["codex", "responses", "login", "vs code extension"],
            StatusSourceRegistry.OpenAi.DefaultComponents);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("CLAUDE")]
    [InlineData("Claude")]
    public void ById_IsCaseInsensitive(string id)
        => Assert.Same(StatusSourceRegistry.Claude, StatusSourceRegistry.ById(id));

    [Theory]
    [InlineData("gemini")]
    [InlineData("")]
    [InlineData(null)]
    public void ById_ReturnsNullForUnknown(string? id) => Assert.Null(StatusSourceRegistry.ById(id));
}
