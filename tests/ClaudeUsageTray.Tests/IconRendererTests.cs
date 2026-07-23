using ClaudeUsageTray.Core;
using ClaudeUsageTray.Tray;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class IconRendererTests
{
    [Theory]
    [InlineData('5', 0, Severity.Green, true, false, 16)]
    [InlineData('5', 42, Severity.Green, true, false, 16)]
    [InlineData('7', 63, Severity.Orange, false, false, 20)]
    [InlineData('7', 100, Severity.Red, false, false, 32)]
    [InlineData('5', 150, Severity.Red, true, false, 16)]  // >100 clamps, must not throw
    [InlineData('5', 42, Severity.Green, true, true, 16)]  // dimmed/stale variant
    public void Render_ProducesIconOfRequestedSize(char digit, int percent, Severity sev, bool cw, bool dimmed, int size)
    {
        using var icon = IconRenderer.Render(digit, percent, sev, cw, dimmed, size);
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

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    public void RenderNeutral_ProducesIcon(int size)
    {
        using var icon = IconRenderer.RenderNeutral(size);
        Assert.Equal(size, icon.Width);
    }

    [Fact]
    public void SystemTrayIconSize_IsAtLeast16()
        => Assert.True(IconRenderer.SystemTrayIconSize() >= 16);
}
