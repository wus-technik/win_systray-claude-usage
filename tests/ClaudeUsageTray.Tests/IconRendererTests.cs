using ClaudeUsageTray.Core;
using ClaudeUsageTray.Tray;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class IconRendererTests
{
    [Theory]
    [InlineData('5', 0, Severity.Green, true, false, 16, false)]
    [InlineData('5', 42, Severity.Green, true, false, 16, true)]
    [InlineData('7', 63, Severity.Orange, false, false, 20, true)]
    [InlineData('7', 100, Severity.Red, false, false, 32, true)]
    [InlineData('5', 150, Severity.Red, true, false, 16, false)]  // >100 clamps, must not throw
    [InlineData('5', 42, Severity.Green, true, true, 16, false)]  // dimmed/stale variant
    [InlineData('5', 42, Severity.Green, true, true, 16, true)]   // dimmed base, badge must still be bright
    public void Render_ProducesIconOfRequestedSize(char digit, int percent, Severity sev, bool cw,
        bool dimmed, int size, bool warning)
    {
        using var icon = IconRenderer.Render(digit, percent, sev, cw, dimmed, size, warning);
        Assert.Equal(size, icon.Width);
        Assert.Equal(size, icon.Height);
    }

    [Fact]
    public void Render_IsNotBlank()
    {
        using var icon = IconRenderer.Render('5', 42, Severity.Green, clockwise: true, dimmed: false, size: 32);
        using var bmp = icon.ToBitmap();
        bool anyPixel = false;
        for (int x = 0; x < bmp.Width && !anyPixel; x++)
            for (int y = 0; y < bmp.Height && !anyPixel; y++)
                if (bmp.GetPixel(x, y).A > 0) anyPixel = true;
        Assert.True(anyPixel, "rendered icon has no visible pixels");
    }

    [Fact]
    public void Render_WithWarningBadge_IsNotBlank()
    {
        using var icon = IconRenderer.Render('5', 42, Severity.Green, clockwise: true, dimmed: true,
            size: 32, warning: true);
        using var bmp = icon.ToBitmap();
        bool anyPixel = false;
        for (int x = 0; x < bmp.Width && !anyPixel; x++)
            for (int y = 0; y < bmp.Height && !anyPixel; y++)
                if (bmp.GetPixel(x, y).A > 0) anyPixel = true;
        Assert.True(anyPixel, "rendered icon has no visible pixels");
    }

    [Theory]
    [InlineData(16, false)]
    [InlineData(24, false)]
    [InlineData(16, true)]
    [InlineData(24, true)]
    public void RenderNeutral_ProducesIcon(int size, bool warning)
    {
        using var icon = IconRenderer.RenderNeutral(size, warning);
        Assert.Equal(size, icon.Width);
    }

    [Fact]
    public void SystemTrayIconSize_IsAtLeast16()
        => Assert.True(IconRenderer.SystemTrayIconSize() >= 16);

    /// <summary>Review Focus 4: a full usage wedge must leave the rounded silhouette's extreme
    /// corners transparent, including at the smallest real tray size.</summary>
    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    public void Render_RoundedSquare_LeavesItsCornersEmpty(int size)
    {
        using var icon = IconRenderer.Render('5', 100, Severity.Red, clockwise: true, dimmed: false,
            size: size, warning: false, shape: BadgeShape.RoundedSquare);
        using var bmp = icon.ToBitmap();

        Assert.Equal(0, bmp.GetPixel(0, 0).A);
        Assert.Equal(0, bmp.GetPixel(bmp.Width - 1, 0).A);
        Assert.Equal(0, bmp.GetPixel(0, bmp.Height - 1).A);
    }

    /// <summary>The two shapes must actually differ, or the provider encoding is decorative. A disc
    /// at 100 % does not fill its corners either, so compare the shapes at a point a rounded square
    /// covers and a disc does not: just inside the top-left corner.</summary>
    [Fact]
    public void Render_ShapesDifferNearTheCorner()
    {
        using var discIcon = IconRenderer.Render('5', 100, Severity.Red, true, false, 32);
        using var squareIcon = IconRenderer.Render('5', 100, Severity.Red, true, false, 32, false,
            BadgeShape.RoundedSquare);
        using var disc = discIcon.ToBitmap();
        using var square = squareIcon.ToBitmap();

        int probe = 4;
        Assert.True(square.GetPixel(probe, probe).A > disc.GetPixel(probe, probe).A,
            "a rounded square must cover more near the corner than a disc");
    }

    /// <summary>The no-data badge carries the shape too: it is the state a newly detected provider
    /// spends its first poll in.</summary>
    [Theory]
    [InlineData(BadgeShape.Disc)]
    [InlineData(BadgeShape.RoundedSquare)]
    public void RenderNeutral_HonoursTheShape(BadgeShape shape)
    {
        using var icon = IconRenderer.RenderNeutral(32, warning: false, shape: shape);
        using var bmp = icon.ToBitmap();

        // (4, 4) lies inside the rounded square but outside the disc at 32 px.
        bool covered = bmp.GetPixel(4, 4).A > 0;
        Assert.Equal(shape == BadgeShape.RoundedSquare, covered);
    }

    /// <summary>The warning badge rides over a rounded square as it does over a disc, and is never
    /// dimmed. Its corner is the bottom-right, which the silhouette change must not disturb.</summary>
    [Fact]
    public void RenderNeutral_RoundedSquareWithWarning_StillDrawsTheMarker()
    {
        using var plainIcon = IconRenderer.RenderNeutral(32, false, BadgeShape.RoundedSquare);
        using var warnedIcon = IconRenderer.RenderNeutral(32, true, BadgeShape.RoundedSquare);
        using var plain = plainIcon.ToBitmap();
        using var warned = warnedIcon.ToBitmap();

        var p = new Point(warned.Width - 6, warned.Height - 6);
        Assert.NotEqual(plain.GetPixel(p.X, p.Y), warned.GetPixel(p.X, p.Y));
    }
}
