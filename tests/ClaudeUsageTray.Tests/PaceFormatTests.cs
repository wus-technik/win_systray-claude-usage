using System.Globalization;
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class PaceFormatTests
{
    [Theory]
    [InlineData(1.44, "1.4× pace")]
    [InlineData(0.76, "0.8× pace")]
    [InlineData(2.666, "2.7× pace")]
    public void DescribesTheRatioToOneDecimal(double ratio, string expected)
        => Assert.Equal(expected, PaceFormat.Describe(ratio));

    [Fact]
    public void UsesAPointWhateverTheUsersLocale()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try { Assert.Equal("1.2× pace", PaceFormat.Describe(1.2)); }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void NoRatioDescribesAsEmpty() => Assert.Equal("", PaceFormat.Describe(null));
}
