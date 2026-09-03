using ClaudeUsageTray.Tray;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>The app icon is a build-time asset, so the way it breaks is silently: a renamed file or
/// a dropped &lt;EmbeddedResource&gt; leaves everything compiling and every window back on the
/// default icon. These assertions are what notices.</summary>
public class AppIconTests
{
    [Fact]
    public void EmbeddedIconLoads()
    {
        Assert.NotNull(AppIcon.Value);
    }

    [Fact]
    public void CarriesEveryShellSize()
    {
        // Read the ICO directory rather than asking GDI+ for each size: a missing entry is not an
        // error there, it silently scales the nearest one, and its best-fit search does not resolve
        // the 256 px entry at all (ICO stores that size as a 0 byte, which is what this decodes).
        using Stream stream = typeof(AppIcon).Assembly
            .GetManifestResourceStream("ClaudeUsageTray.app.ico")!;
        using var reader = new BinaryReader(stream);
        reader.ReadUInt16();                        // reserved
        Assert.Equal(1, reader.ReadUInt16());       // type: 1 = icon
        int count = reader.ReadUInt16();

        var sizes = new List<int>();
        for (int i = 0; i < count; i++)
        {
            int width = reader.ReadByte();
            reader.ReadBytes(15);                   // height, palette, planes, bpp, length, offset
            sizes.Add(width == 0 ? 256 : width);
        }

        Assert.Equal(new[] { 16, 24, 32, 48, 64, 128, 256 }, sizes.Order());
    }
}
