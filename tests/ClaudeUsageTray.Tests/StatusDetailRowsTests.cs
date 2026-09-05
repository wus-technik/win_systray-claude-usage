using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class StatusDetailRowsTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyList<string> Codex = ["codex"];
    private static readonly StatusSource OpenAi = StatusSourceRegistry.OpenAi;

    private static PlatformStatus Status(PlatformComponent[]? components = null,
        PlatformIncident[]? incidents = null, string indicator = "minor",
        string description = "Partial System Outage")
        => new("openai", Now, indicator, description, incidents ?? [], components ?? []);

    private static PlatformIncident Incident(string name, string? shortlink = null,
        params string[] components)
        => new(name, "investigating", "minor", shortlink, Now.AddMinutes(-30), components);

    [Fact]
    public void Components_BecomeRowsWithUnfoldedStatus()
    {
        var rows = StatusDetail.Rows(
            Status([new("Codex API", "degraded_performance")]), Codex, Now, max: 3);
        Assert.Equal("Codex API — Degraded performance", Assert.Single(rows).Text);
        Assert.Null(rows[0].Link);
    }

    [Fact]
    public void ComponentRows_AreFilteredToWatchedOnes()
    {
        var rows = StatusDetail.Rows(
            Status([new("Sora", "major_outage"), new("Codex Web", "partial_outage")]), Codex, Now, max: 3);
        Assert.Equal(["Codex Web — Partial outage"], rows.Select(r => r.Text));
    }

    [Fact]
    public void Incidents_WinOverComponents_AndKeepTheirShortlink()
    {
        var rows = StatusDetail.Rows(
            Status([new("Codex API", "major_outage")], [Incident("Elevated errors", "https://stspg.io/x")]),
            Codex, Now, max: 3);
        var row = Assert.Single(rows);
        Assert.StartsWith("Elevated errors — Investigating · minor", row.Text);
        Assert.Contains("updated ", row.Text);
        Assert.Equal("https://stspg.io/x", row.Link);
    }

    /// <summary>Precedence runs after filtering: incidents that are all filtered out must not
    /// suppress the component rows that do match, or the source shows degraded with zero rows.</summary>
    [Fact]
    public void IncidentsAllFilteredOut_FallsBackToWatchedComponents()
    {
        var rows = StatusDetail.Rows(
            Status([new("Codex API", "major_outage")], [Incident("Sora is down", null, "Sora")]),
            Codex, Now, max: 3);
        Assert.Equal(["Codex API — Major outage"], rows.Select(r => r.Text));
    }

    [Fact]
    public void Rows_AreCappedAndTheRestCounted()
    {
        var status = Status([
            new("Codex API", "major_outage"), new("Codex Web", "major_outage"),
            new("Codex in ChatGPT Desktop", "major_outage"), new("Codex CLI", "major_outage")]);
        Assert.Equal(3, StatusDetail.Rows(status, Codex, Now, max: 3).Count);
        Assert.Equal(1, StatusDetail.HiddenCount(status, Codex, max: 3));
    }

    [Fact]
    public void NothingIdentified_YieldsNoRows()
    {
        Assert.Empty(StatusDetail.Rows(Status(), Codex, Now, max: 3));
        Assert.Equal(0, StatusDetail.HiddenCount(Status(), Codex, max: 3));
    }

    [Fact]
    public void Header_UsesTheSourceNameAndThePagesOwnWords()
        => Assert.Equal("OpenAI status: Partial System Outage",
            StatusDetail.Header(OpenAi, Status(), relevant: true, stale: false));

    [Fact]
    public void Header_FallsBackToTheIndicatorWhenTheBannerIsEmpty()
        => Assert.Equal("OpenAI status: minor",
            StatusDetail.Header(OpenAi, Status(description: ""), relevant: true, stale: false));

    [Fact]
    public void Header_ExplainsAFilteredOutDisruption()
        => Assert.Equal("OpenAI status: Partial System Outage · outside your watched components",
            StatusDetail.Header(OpenAi, Status(), relevant: false, stale: false));

    [Fact]
    public void Header_MarksStaleAndNoData()
    {
        Assert.Equal("OpenAI status: Partial System Outage · stale",
            StatusDetail.Header(OpenAi, Status(), relevant: true, stale: true));
        Assert.Equal("OpenAI status: unavailable",
            StatusDetail.Header(OpenAi, null, relevant: false, stale: false));
    }
}
