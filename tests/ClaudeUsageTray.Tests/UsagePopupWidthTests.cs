using ClaudeUsageTray.Core;
using ClaudeUsageTray.Tray;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>The popup is AutoSize and PositionNearCursor clamps its position but not its size, so
/// page-supplied text — banner wording and incident detail, both unbounded in length — must wrap
/// instead of stretching the form sideways off the screen edge.</summary>
public class UsagePopupWidthTests : IDisposable
{
    private readonly List<UsagePopup> _open = [];

    public void Dispose() { foreach (var popup in _open) popup.Dispose(); }

    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    private static readonly UsageSnapshot Snapshot =
        new(Now.AddMinutes(-1), new WindowUsage(20, Now.AddHours(3)), new WindowUsage(31, Now.AddDays(4)));

    private int WidthWith(PlatformStatus? status)
    {
        var popup = new UsagePopup(new DisplayChoice(Snapshot, false), new Settings(), Now, status);
        _open.Add(popup);
        popup.PerformLayout();
        return popup.PreferredSize.Width;
    }

    private static PlatformStatus Degraded(string description, params PlatformIncident[] incidents)
        => new(Now, "major", description, incidents);

    private static PlatformIncident Incident(string name, IReadOnlyList<string>? components = null)
        => new(name, "identified", "major", "https://stspg.io/x", Now.AddMinutes(-7), components ?? []);

    /// <summary>The usage rows are what the width is meant to be governed by: a bar plus its caption.
    /// Anything the status page sends has to fit inside that, however long it is.</summary>
    [Fact]
    public void LongIncidentTextDoesNotWidenThePopup()
    {
        var baseline = WidthWith(Degraded("Elevated errors", Incident("Short")));

        var wide = WidthWith(Degraded("Elevated errors", Incident(
            "Elevated error rates and substantially increased latency affecting the Messages API, "
            + "streaming responses, and tool use across all first-party surfaces")));

        Assert.Equal(baseline, wide);
    }

    /// <summary>The banner is the page's own wording and is shown verbatim, so it is unbounded too.</summary>
    [Fact]
    public void LongBannerTextDoesNotWidenThePopup()
    {
        var baseline = WidthWith(Degraded("Elevated errors", Incident("Short")));

        var wide = WidthWith(Degraded(
            "Partially degraded service across the Claude API, Claude Code, and claude.ai while we "
            + "investigate elevated error rates in one upstream region",
            Incident("Short")));

        Assert.Equal(baseline, wide);
    }

    /// <summary>A long component list is appended to the same incident line and would otherwise
    /// stretch it just as far as a long name.</summary>
    [Fact]
    public void ManyAffectedComponentsDoNotWidenThePopup()
    {
        var baseline = WidthWith(Degraded("Elevated errors", Incident("Short")));

        var wide = WidthWith(Degraded("Elevated errors", Incident("Short",
            ["Claude API", "Claude Code", "claude.ai", "Console", "Admin API", "Batch API"])));

        Assert.Equal(baseline, wide);
    }

    /// <summary>Wrapping must add height, not silently clip the text to one line.</summary>
    [Fact]
    public void LongIncidentTextGrowsThePopupDownwards()
    {
        var baselinePopup = new UsagePopup(new DisplayChoice(Snapshot, false), new Settings(), Now,
            Degraded("Elevated errors", Incident("Short")));
        _open.Add(baselinePopup);
        baselinePopup.PerformLayout();
        var baseline = baselinePopup.PreferredSize.Height;

        var widePopup = new UsagePopup(new DisplayChoice(Snapshot, false), new Settings(), Now, Degraded("Elevated errors", Incident(
            "Elevated error rates and substantially increased latency affecting the Messages API, "
            + "streaming responses, and tool use across all first-party surfaces")));
        _open.Add(widePopup);
        widePopup.PerformLayout();

        Assert.True(widePopup.PreferredSize.Height > baseline,
            $"expected the wrapped incident to be taller than {baseline}, was {widePopup.PreferredSize.Height}");
    }

    /// <summary>The last-updated line is app-authored text, but "Claude Desktop history · updated
    /// 5h ago · stale" is still longer than the plain Claude Code line and must not stretch the
    /// popup past what the bar governs.</summary>
    [Fact]
    public void DesktopSourceUpdatedLineDoesNotWidenThePopup()
    {
        var baseline = WidthWith(new DisplayChoice(Snapshot, false), null);

        var desktop = new UsageSnapshot(Now.AddHours(-5), new WindowUsage(20, null), new WindowUsage(31, null))
        {
            Source = UsageSource.DesktopHistory,
        };
        var wide = WidthWith(new DisplayChoice(desktop, true), null);

        Assert.True(wide <= baseline,
            $"expected the desktop-source popup ({wide}) to be no wider than the CLI baseline ({baseline})");
    }

    private int WidthWith(DisplayChoice choice, PlatformStatus? status)
    {
        var popup = new UsagePopup(choice, new Settings(), Now, status);
        _open.Add(popup);
        popup.PerformLayout();
        return popup.PreferredSize.Width;
    }
}
