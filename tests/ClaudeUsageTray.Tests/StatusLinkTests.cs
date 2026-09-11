using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class StatusLinkTests
{
    private static readonly StatusSource Claude = StatusSourceRegistry.Claude;
    private static readonly StatusSource OpenAi = StatusSourceRegistry.OpenAi;

    [Theory]
    // The shape Statuspage actually sends, for both sources.
    [InlineData("https://stspg.io/abc123")]
    [InlineData("https://status.claude.com/incidents/abc123")]
    // A subdomain of the page host is still the page's own operator.
    [InlineData("https://www.status.claude.com/incidents/abc123")]
    public void LinksOnAnAllowedHostSurvive(string url)
        => Assert.Equal(url, StatusLink.Safe(url, Claude));

    [Fact]
    public void TheShortenerIsAllowedForEverySource()
        => Assert.Equal("https://stspg.io/x", StatusLink.Safe("https://stspg.io/x", OpenAi));

    [Fact]
    public void OneSourceMayNotLinkToAnothersPage()
    {
        Assert.Null(StatusLink.Safe("https://status.claude.com/incidents/x", OpenAi));
        Assert.Equal("https://status.openai.com/incidents/x",
            StatusLink.Safe("https://status.openai.com/incidents/x", OpenAi));
    }

    [Theory]
    // Anything but https, however harmless it looks: the popup shell-executes what it gets.
    [InlineData("http://stspg.io/x")]
    [InlineData("file://server/share/payload.exe")]
    [InlineData(@"\\server\share\payload.exe")]
    [InlineData("ms-settings:windowsupdate")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    // Hosts the source has no business linking to.
    [InlineData("https://evil.example/x")]
    [InlineData("https://notstspg.io/x")]
    [InlineData("https://stspg.io.evil.example/x")]
    // Ways to make a bad host read as a good one, or to reach something other than 443.
    [InlineData("https://stspg.io@evil.example/x")]
    [InlineData("https://stspg.io:8443/x")]
    // Not a link at all.
    [InlineData("/incidents/x")]
    [InlineData("stspg.io/x")]
    [InlineData("")]
    [InlineData("   ")]
    public void EverythingElseIsDropped(string url) => Assert.Null(StatusLink.Safe(url, Claude));

    [Fact]
    public void NullIsDroppedRatherThanThrowing() => Assert.Null(StatusLink.Safe(null, Claude));
}
