using System.Windows.Forms;
using ClaudeUsageTray.Core;
using ClaudeUsageTray.Tray;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>What the popup says about where its numbers came from, and what it says when there are none.</summary>
public class UsagePopupSourceTests : IDisposable
{
    private readonly List<UsagePopup> _open = [];
    public void Dispose() { foreach (var popup in _open) popup.Dispose(); }

    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private UsagePopup Popup(DisplayChoice choice, string? noDataText = null)
    {
        var popup = new UsagePopup(choice, new Settings(), Now, null, null, noDataText);
        _open.Add(popup);
        popup.PerformLayout();
        return popup;
    }

    private static IEnumerable<string> Texts(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is Label l) yield return l.Text;
            foreach (var t in Texts(c)) yield return t;
        }
    }

    private static UsageSnapshot Desktop(TimeSpan age, WindowUsage? five, WindowUsage? seven, CreditUsage? credits = null)
        => new(Now - age, five, seven, [], credits) { Source = UsageSource.DesktopHistory };

    [Fact]
    public void DesktopSource_NamesItselfInTheUpdatedLine()
    {
        var popup = Popup(new DisplayChoice(Desktop(TimeSpan.FromMinutes(40), new(7, null), new(17, null)), false));
        Assert.Contains("Claude Desktop history · updated 40m ago", Texts(popup));
    }

    [Fact]
    public void DesktopSource_Stale_IsFlagged()
    {
        var popup = Popup(new DisplayChoice(Desktop(TimeSpan.FromHours(5), new(7, null), new(17, null)), true));
        Assert.Contains("Claude Desktop history · updated 5h ago · stale", Texts(popup));
    }

    [Fact]
    public void ClaudeCodeSource_KeepsTheExistingWording()
    {
        var cli = new UsageSnapshot(Now.AddMinutes(-2), new(7, Now.AddHours(1)), new(17, Now.AddDays(1)));
        var popup = Popup(new DisplayChoice(cli, false));
        Assert.Contains("Last updated 2m ago", Texts(popup));
    }

    [Fact]
    public void StaleFlag_ComesFromTheChoice_NotFromStalenessMinutes()
    {
        // 40 min is past the 15 min default, but the choice says fresh — the desktop allowance decided.
        var popup = Popup(new DisplayChoice(Desktop(TimeSpan.FromMinutes(40), new(7, null), new(17, null)), false));
        Assert.DoesNotContain(Texts(popup), t => t.EndsWith("· stale"));
    }

    [Fact]
    public void DesktopSource_OneWindowNullAndPercentOnlyCredits_Render()
    {
        var credits = new CreditUsage(null, null, 67, null, new CreditState(true, null, false));
        var popup = Popup(new DisplayChoice(Desktop(TimeSpan.FromMinutes(1), null, new(17, null), credits), false));
        var texts = Texts(popup).ToList();
        Assert.Contains("5-hour window: no data", texts);
        Assert.Contains(texts, t => t.StartsWith("7-day window — 17%"));
        Assert.Contains("Credits — 67%", texts);
    }

    [Fact]
    public void NoData_ShowsTheGivenReason()
    {
        var popup = Popup(new DisplayChoice(null, false),
            "Claude Code has not cached usage data, and there is no credentials file for a live fetch.");
        Assert.Contains("Claude Code has not cached usage data, and there is no credentials file for a live fetch.", Texts(popup));
    }

    [Fact]
    public void NoData_WithoutAReason_FallsBackToTheDefaultLine()
        => Assert.Contains(NoDataReason.Default, Texts(Popup(new DisplayChoice(null, false))));

    [Fact]
    public void NoDataText_WrapsInsteadOfWideningThePopup()
    {
        var shortPopup = Popup(new DisplayChoice(null, false), "Short.");
        var longPopup = Popup(new DisplayChoice(null, false),
            "Claude Code has not cached usage data, and its credentials are not usable for a live fetch, "
            + "and this sentence keeps going to make sure the label wraps rather than stretches.");
        Assert.True(longPopup.PreferredSize.Width <= Math.Max(shortPopup.PreferredSize.Width, UsageBar.DefaultWidth + 40),
            $"long={longPopup.PreferredSize.Width} short={shortPopup.PreferredSize.Width}");
    }
}
