using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class StatusDetailRelevanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyList<string> Codex = ["codex"];

    private static PlatformStatus Status(string indicator, PlatformComponent[]? components = null,
        PlatformIncident[]? incidents = null)
        => new("openai", Now, indicator, indicator == "none" ? "All Systems Operational" : "Partial System Outage",
            incidents ?? [], components ?? []);

    private static PlatformIncident Incident(params string[] components)
        => new("Something broke", "investigating", "minor", null, Now, components);

    [Fact]
    public void Operational_IsNeverRelevant()
        => Assert.False(StatusDetail.IsRelevant(Status("none"), Codex));

    [Fact]
    public void EmptyFilter_MakesAnyDegradationRelevant()
        => Assert.True(StatusDetail.IsRelevant(Status("minor", [new("Sora", "major_outage")]), []));

    [Fact]
    public void WatchedComponentAffected_IsRelevant()
        => Assert.True(StatusDetail.IsRelevant(Status("minor", [new("Codex API", "partial_outage")]), Codex));

    [Fact]
    public void OnlyUnwatchedComponentsAffected_IsNotRelevant()
        => Assert.False(StatusDetail.IsRelevant(Status("minor", [new("Sora", "major_outage")]), Codex));

    /// <summary>The failure this rule exists for: a page can report a disruption while every
    /// component still reads operational and no incident names one. Hiding that behind a
    /// noise-reduction filter would hide a real outage.</summary>
    [Fact]
    public void DegradedButUnclassifiable_IsAlwaysRelevant()
        => Assert.True(StatusDetail.IsRelevant(Status("major"), Codex));

    [Fact]
    public void IncidentNamingNoComponents_CountsAsWatched()
        => Assert.True(StatusDetail.IsRelevant(Status("minor", [new("Sora", "major_outage")], [Incident()]), Codex));

    [Fact]
    public void IncidentNamingOnlyUnwatchedComponents_IsNotRelevant()
        => Assert.False(StatusDetail.IsRelevant(
            Status("minor", [new("Sora", "major_outage")], [Incident("Sora")]), Codex));

    [Fact]
    public void IncidentNamingAWatchedComponent_IsRelevant()
        => Assert.True(StatusDetail.IsRelevant(Status("minor", incidents: [Incident("Codex API")]), Codex));

    [Theory]
    [InlineData("minor", StatusEmphasis.Warning)]
    [InlineData("major", StatusEmphasis.Alert)]
    [InlineData("critical", StatusEmphasis.Alert)]
    [InlineData("something_new", StatusEmphasis.Alert)]
    public void RelevantDegradation_IsEmphasised(string indicator, StatusEmphasis expected)
        => Assert.Equal(expected, StatusDetail.Emphasis(Status(indicator), relevant: true));

    [Fact]
    public void IrrelevantDegradation_AndHealth_AndNoData_AreMuted()
    {
        Assert.Equal(StatusEmphasis.Muted, StatusDetail.Emphasis(Status("minor"), relevant: false));
        Assert.Equal(StatusEmphasis.Muted, StatusDetail.Emphasis(Status("none"), relevant: false));
        Assert.Equal(StatusEmphasis.Muted, StatusDetail.Emphasis(null, relevant: false));
    }
}
