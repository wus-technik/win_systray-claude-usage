using ClaudeUsageTray.Core;
using ClaudeUsageTray.Tray;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>CreateControl, never Show: OnDeactivate calls Close, and with no message loop Show
/// disposes the form and leaves a blank render with no controls and no error.</summary>
public class UsagePopupCodexTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static DisplayChoice Claude()
        => new(new UsageSnapshot(Now, new WindowUsage(42, Now.AddHours(3)),
            new WindowUsage(21, Now.AddDays(4))), Stale: false);

    private static DisplayChoice Codex()
        => new(new UsageSnapshot(Now, new WindowUsage(55, Now.AddHours(2)),
            new WindowUsage(33, Now.AddDays(5)),
            [new ScopedLimit("Spend control", null, 111, Now.AddDays(19), true)
                { Period = ScopedPeriod.Monthly }])
            { Source = UsageSource.Codex }, Stale: false);

    private static IEnumerable<string> Captions(Control root)
    {
        foreach (var control in root.Controls.Cast<Control>())
        {
            if (control is Label label) yield return label.Text;
            foreach (var nested in Captions(control)) yield return nested;
        }
    }

    /// <summary>A Claude-only popup is byte-for-byte what it was: no headers, no separator, no
    /// visual churn for a user who has never run Codex.</summary>
    [Fact]
    public void WithoutCodex_NoProviderHeaders()
    {
        using var popup = new UsagePopup(Claude(), new Settings(), Now);
        popup.CreateControl();

        var captions = Captions(popup).ToList();
        Assert.Contains(captions, c => c.Contains("5-hour window"));
        Assert.DoesNotContain(captions, c => c.Contains("Codex"));
        Assert.DoesNotContain(captions, c => c.Trim() == "Claude");
    }

    [Fact]
    public void WithCodex_ShowsBothSectionsWithHeaders()
    {
        using var popup = new UsagePopup(Claude(), new Settings(), Now, codex: Codex(), codexFetchStatus: "live");
        popup.CreateControl();

        var captions = Captions(popup).ToList();
        Assert.Contains(captions, c => c.Contains("Claude"));
        Assert.Contains(captions, c => c.Contains("Codex"));
        Assert.Equal(2, captions.Count(c => c.Contains("5-hour window")));
    }

    [Fact]
    public void WithCodex_ShowsTheSpendControlRowAsMonthly()
    {
        using var popup = new UsagePopup(Claude(), new Settings(), Now, codex: Codex(), codexFetchStatus: "live");
        popup.CreateControl();

        Assert.Contains(Captions(popup), c => c.Contains("Spend control monthly") && c.Contains("111%"));
    }

    /// <summary>Each section owns its own freshness line: the pipelines have independent cadences, so
    /// one shared footer would misreport one of them.</summary>
    [Fact]
    public void WithCodex_EachSectionHasItsOwnUpdatedLine()
    {
        var staleCodex = new DisplayChoice(Codex().Snapshot, Stale: true);
        using var popup = new UsagePopup(Claude(), new Settings(), Now, codex: staleCodex, codexFetchStatus: "live");
        popup.CreateControl();

        var captions = Captions(popup).ToList();
        Assert.Equal(2, captions.Count(c => c.Contains("updated", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(captions, c => c.Contains("stale"));
    }

    /// <summary>No credits row for Codex, whatever the popup does for Claude: the payload has no
    /// percentage to draw and a 0 % bar is the placeholder the invariants forbid.</summary>
    [Fact]
    public void WithCodex_NoCreditsRow()
    {
        using var popup = new UsagePopup(Claude(), new Settings(), Now, codex: Codex(), codexFetchStatus: "live");
        popup.CreateControl();

        Assert.DoesNotContain(Captions(popup), c => c.StartsWith("Credits"));
    }

    [Fact]
    public void WithCodex_FetchOutcomesBelongToTheirOwnSections()
    {
        using var popup = new UsagePopup(Claude(), new Settings(), Now,
            lastFetchStatus: "Claude unavailable", codex: Codex(), codexFetchStatus: "Codex live");
        popup.CreateControl();

        var captions = Captions(popup).ToList();
        int codexHeader = captions.IndexOf("Codex");
        Assert.InRange(captions.IndexOf("Fetch: Claude unavailable"), 0, codexHeader - 1);
        Assert.True(captions.IndexOf("Fetch: Codex live") > codexHeader);
    }

    [Fact]
    public void WithBothProviders_OverflowKeepsAllRowsReachable()
    {
        using var popup = new UsagePopup(Claude(), new Settings(), Now,
            codex: Codex(), maxHeight: 200);
        popup.CreateControl();

        var host = popup.Controls.Find("scrollHost", true).OfType<Panel>().Single();
        Assert.True(popup.Height <= 200);
        Assert.True(host.AutoScroll);
        Assert.False(host.HorizontalScroll.Visible);
        Assert.Contains(Captions(popup), c => c.Contains("Spend control monthly"));
    }
}
