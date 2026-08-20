using System.Drawing;
using ClaudeUsageTray.Core;
using ClaudeUsageTray.Tray;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>The bar drawing shared by the popup rows and the settings preview. Painted to a bitmap
/// and sampled, so the preview provably shows the same thing the popup does.</summary>
public class UsageBarTests
{
    private const int Width = UsageBar.DefaultWidth;
    private const int Height = UsageBar.DefaultHeight;

    private static Bitmap Paint(int percent, Severity severity, double? elapsedFraction = null)
    {
        var bitmap = new Bitmap(Width, Height);
        using var g = Graphics.FromImage(bitmap);
        UsageBar.Paint(g, Width, Height, percent, severity, elapsedFraction);
        return bitmap;
    }

    [Theory]
    [InlineData(Severity.Green)]
    [InlineData(Severity.Orange)]
    [InlineData(Severity.Red)]
    public void FillUsesTheSeverityColour(Severity severity)
    {
        using var bitmap = Paint(50, severity);
        Assert.Equal(UsageBar.ColorFor(severity).ToArgb(), bitmap.GetPixel(10, Height / 2).ToArgb());
    }

    [Fact]
    public void FillStopsAtThePercent()
    {
        using var bitmap = Paint(50, Severity.Green);
        Assert.Equal(UsageBar.ColorFor(Severity.Green).ToArgb(), bitmap.GetPixel(Width / 2 - 2, Height / 2).ToArgb());
        Assert.NotEqual(UsageBar.ColorFor(Severity.Green).ToArgb(), bitmap.GetPixel(Width / 2 + 2, Height / 2).ToArgb());
    }

    [Theory]
    [InlineData(-20)]
    [InlineData(140)]
    public void OutOfRangePercentsStayInsideTheBar(int percent)
    {
        using var bitmap = Paint(percent, Severity.Red); // must not throw on a negative width
        Assert.Equal(UsageBar.ColorFor(Severity.Red).ToArgb() == bitmap.GetPixel(10, Height / 2).ToArgb(),
            percent > 0);
    }

    [Fact]
    public void MarkerSpansEveryRowTheBorderLeaves()
    {
        // The border is drawn last and owns y=0 and y=Height-1, so a full-height marker still shows
        // up as Height-2 rows. That is the point of the band: a 3px inset notch had almost nothing
        // left after the border took its share.
        using var bitmap = Paint(0, Severity.Green, 0.5);
        var marker = SystemColors.ControlText.ToArgb();
        var x = 1 + (int)Math.Round((Width - 2 - 2) * 0.5);
        for (int y = 1; y < Height - 1; y++)
            Assert.Equal(marker, bitmap.GetPixel(x, y).ToArgb());
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void MarkerSurvivesTheBorderAtBothEnds(double fraction)
    {
        // The border owns x=0 and x=Width-1 and would otherwise swallow the marker at the extremes.
        using var bitmap = Paint(0, Severity.Green, fraction);
        var marker = SystemColors.ControlText.ToArgb();
        bool found = false;
        for (int x = 0; x < Width && !found; x++) found = bitmap.GetPixel(x, Height / 2).ToArgb() == marker;
        Assert.True(found, $"no marker column at fraction {fraction}");
    }

    [Fact]
    public void NoMarkerWithoutAFraction()
    {
        using var bitmap = Paint(0, Severity.Green);
        for (int x = 1; x < Width - 1; x++)
            Assert.NotEqual(SystemColors.ControlText.ToArgb(), bitmap.GetPixel(x, Height / 2).ToArgb());
    }
}
