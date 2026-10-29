using ClaudeUsageTray.Core;
using ClaudeUsageTray.Tray;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>Review Focus 5: more rows than there is room for. CreateControl, never Show —
/// UsagePopup.OnDeactivate calls Close, and with no message loop Show disposes the form mid-test.
/// The cap is injected rather than taken from the screen, so these assertions mean the same thing on
/// a 1080p laptop and a 4K monitor.</summary>
public class UsagePopupHeightTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static DisplayChoice Crowded()
    {
        var scoped = Enumerable.Range(0, 12)
            .Select(i => new ScopedLimit($"Limit {i}", null, 50 + i, Now.AddDays(2), IsActive: true))
            .ToList();
        return new(new UsageSnapshot(Now, new WindowUsage(42, Now.AddHours(3)),
            new WindowUsage(21, Now.AddDays(4)), scoped), Stale: false);
    }

    private static DisplayChoice Small()
        => new(new UsageSnapshot(Now, new WindowUsage(42, Now.AddHours(3)),
            new WindowUsage(21, Now.AddDays(4))), Stale: false);

    private static Panel Host(UsagePopup popup)
        => popup.Controls.Find("scrollHost", searchAllChildren: true).OfType<Panel>().Single();

    /// <summary>The actual size, not PreferredSize: Form.PreferredSize is GetPreferredSize(Empty)
    /// and ignores the clamp entirely, so asserting on it would pass whatever the clamp did.</summary>
    [Fact]
    public void Popup_OverTheCap_ClampsItsHeight()
    {
        using var popup = new UsagePopup(Crowded(), new Settings(), Now, maxHeight: 200);
        popup.CreateControl();

        Assert.True(popup.Height <= 200, $"popup is {popup.Height} px, cap was 200 px");
    }

    [Fact]
    public void Popup_OverTheCap_Scrolls()
    {
        using var popup = new UsagePopup(Crowded(), new Settings(), Now, maxHeight: 200);
        popup.CreateControl();

        var host = Host(popup);
        Assert.True(host.AutoScroll);
        Assert.True(host.AutoScrollMinSize.Height > host.ClientSize.Height
                    || host.VerticalScroll.Visible,
            $"content {host.AutoScrollMinSize.Height} px must exceed viewport {host.ClientSize.Height} px");
    }

    /// <summary>The vertical scrollbar needs its own gutter; freezing the form's default width
    /// before its first handle exists would clip captions and add a horizontal scrollbar.</summary>
    [Fact]
    public void Popup_OverTheCap_PreservesTheContentWidth()
    {
        using var popup = new UsagePopup(Crowded(), new Settings(), Now, maxHeight: 200);
        popup.CreateControl();

        var host = Host(popup);
        var layout = host.Controls.OfType<TableLayoutPanel>().Single();
        Assert.True(host.ClientSize.Width >= layout.PreferredSize.Width,
            $"viewport {host.ClientSize.Width} px must fit content {layout.PreferredSize.Width} px");
        Assert.False(host.HorizontalScroll.Visible);
    }

    /// <summary>Under the cap nothing changes: no scrollbar, no reserved gutter, and the form still
    /// sizes itself to its content. The common case is two windows and it looked right before.</summary>
    [Fact]
    public void Popup_UnderTheCap_DoesNotScroll()
    {
        using var popup = new UsagePopup(Small(), new Settings(), Now, maxHeight: 2000);
        popup.CreateControl();

        Assert.False(Host(popup).AutoScroll);
        Assert.True(popup.Height < 2000);
    }

    /// <summary>And the clamp engages off the real screen when no cap is passed, which is how the app
    /// calls it.</summary>
    [Fact]
    public void Popup_WithNoCap_StaysWithinTheWorkingArea()
    {
        using var popup = new UsagePopup(Crowded(), new Settings(), Now);
        popup.CreateControl();

        Assert.True(popup.Height <= Screen.PrimaryScreen!.WorkingArea.Height);
    }
}
