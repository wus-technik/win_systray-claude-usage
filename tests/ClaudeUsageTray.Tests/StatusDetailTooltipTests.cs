using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class StatusDetailTooltipTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    private static SourceView View(StatusSource source, string indicator, string description,
        int ageMinutes = 0, IReadOnlyList<string>? filter = null)
        => new(source, new PlatformStatus(source.Id, Now.AddMinutes(-ageMinutes), indicator, description, [], []),
            filter ?? []);

    private static string Suffix(int available, params SourceView[] views)
        => StatusDetail.TooltipSuffix(views, Now, stalenessMinutes: 15, available);

    [Fact]
    public void HealthyAndMissingSources_AddNothing()
    {
        Assert.Equal("", Suffix(100,
            View(StatusSourceRegistry.Claude, "none", "All Systems Operational"),
            new SourceView(StatusSourceRegistry.OpenAi, null, [])));
    }

    [Fact]
    public void DegradedSource_NamesItself()
        => Assert.Equal(" · OpenAI: Partial System Outage",
            Suffix(100, View(StatusSourceRegistry.OpenAi, "minor", "Partial System Outage")));

    [Fact]
    public void StaleDegradation_IsMarked()
        => Assert.Equal(" · Claude: Partial outage (stale)",
            Suffix(100, View(StatusSourceRegistry.Claude, "major", "Partial outage", ageMinutes: 60)));

    [Fact]
    public void FilteredOutDegradation_AddsNothing()
        => Assert.Equal("", Suffix(100,
            new SourceView(StatusSourceRegistry.OpenAi,
                new PlatformStatus("openai", Now, "minor", "Partial System Outage", [],
                    [new PlatformComponent("Sora", "major_outage")]),
                ["codex"])));

    [Fact]
    public void BadgeRaisingSource_ComesFirst()
        => Assert.Equal(" · Claude: Major outage · OpenAI: Partial System Outage",
            Suffix(100,
                View(StatusSourceRegistry.OpenAi, "minor", "Partial System Outage"),
                View(StatusSourceRegistry.Claude, "major", "Major outage")));

    /// <summary>Trim order cannot protect the badge-raising suffix — TrimTooltip cuts the finished
    /// string. So the non-badge suffix is dropped whole rather than half-rendered.</summary>
    [Fact]
    public void NonBadgeSuffix_IsDroppedWholeWhenItDoesNotFit()
    {
        var claudeOnly = " · Claude: Major outage";
        Assert.Equal(claudeOnly, Suffix(claudeOnly.Length + 5,
            View(StatusSourceRegistry.Claude, "major", "Major outage"),
            View(StatusSourceRegistry.OpenAi, "minor", "Partial System Outage")));
    }

    [Fact]
    public void BadgeSuffix_IsKeptEvenWhenItAloneOverflows()
    {
        Assert.Equal(" · Claude: Major outage",
            Suffix(0, View(StatusSourceRegistry.Claude, "major", "Major outage")));
    }

    private static string Compose(string usageText, params SourceView[] views)
        => StatusDetail.ComposeTooltip(usageText, views, Now, stalenessMinutes: 15);

    [Fact]
    public void Compose_ShortTextGetsEverySuffix()
        => Assert.Equal("5h 40% · Claude: Major outage · OpenAI: Partial System Outage",
            Compose("5h 40%",
                View(StatusSourceRegistry.OpenAi, "minor", "Partial System Outage"),
                View(StatusSourceRegistry.Claude, "major", "Major outage")));

    /// <summary>100 + 23 fits; the 32-character OpenAI piece would not, so it is dropped whole and
    /// the usage text is left alone.</summary>
    [Fact]
    public void Compose_DropsTheNonBadgeSuffixBeforeTouchingTheUsageText()
    {
        var usage = new string('x', 100);
        Assert.Equal(usage + " · Claude: Major outage",
            Compose(usage,
                View(StatusSourceRegistry.Claude, "major", "Major outage"),
                View(StatusSourceRegistry.OpenAi, "minor", "Partial System Outage")));
    }

    /// <summary>The badge-raising suffix is the text that explains the marker on the icon. When even
    /// it does not fit, the usage text is what gets shortened — TrayApp.TrimTooltip would cut the
    /// suffix instead.</summary>
    [Fact]
    public void Compose_ShortensTheUsageTextToKeepTheBadgeSuffix()
    {
        var text = Compose(new string('x', 120), View(StatusSourceRegistry.Claude, "major", "Major outage"));
        Assert.True(text.Length <= StatusDetail.TooltipLimit);
        Assert.EndsWith("… · Claude: Major outage", text);
        Assert.StartsWith("xxxx", text);
    }

    [Fact]
    public void Compose_WithNoRelevantSource_ReturnsTheUsageTextUnchanged()
        => Assert.Equal("5h 40%", Compose("5h 40%",
            View(StatusSourceRegistry.Claude, "none", "All Systems Operational")));
}
