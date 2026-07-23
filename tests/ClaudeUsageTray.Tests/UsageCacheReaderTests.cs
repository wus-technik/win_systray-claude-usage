using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class UsageCacheReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cut-reader-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteFixture(string json)
    {
        var path = Path.Combine(_dir, ".claude.json");
        File.WriteAllText(path, json);
        return path;
    }

    private const string ValidJson = """
        {
          "otherTopLevelKey": true,
          "cachedUsageUtilization": {
            "fetchedAtMs": 1784815176543,
            "utilization": {
              "five_hour": { "utilization": 42, "resets_at": "2026-07-23T18:39:59Z" },
              "seven_day": { "utilization": 13, "resets_at": "2026-07-27T15:59:59Z" }
            },
            "extra_usage": {}, "spend": 0
          }
        }
        """;

    [Fact]
    public void Valid_ParsesBothWindows()
    {
        var s = UsageCacheReader.TryRead(WriteFixture(ValidJson));
        Assert.NotNull(s);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1784815176543), s!.FetchedAt);
        Assert.Equal(42, s.FiveHour!.Percent);
        Assert.Equal(new DateTimeOffset(2026, 7, 23, 18, 39, 59, TimeSpan.Zero), s.FiveHour.ResetsAt);
        Assert.Equal(13, s.SevenDay!.Percent);
        Assert.Equal(new DateTimeOffset(2026, 7, 27, 15, 59, 59, TimeSpan.Zero), s.SevenDay.ResetsAt);
    }

    [Fact]
    public void MissingFile_ReturnsNull()
        => Assert.Null(UsageCacheReader.TryRead(Path.Combine(_dir, "does-not-exist.json")));

    [Fact]
    public void MissingCachedUsageKey_ReturnsNull()
        => Assert.Null(UsageCacheReader.TryRead(WriteFixture("""{ "someOtherKey": 1 }""")));

    [Fact]
    public void MalformedJson_ReturnsNull()
        => Assert.Null(UsageCacheReader.TryRead(WriteFixture("{ not json !!")));

    [Fact]
    public void MissingFetchedAtMs_ReturnsNull()
        => Assert.Null(UsageCacheReader.TryRead(WriteFixture(
            """{ "cachedUsageUtilization": { "utilization": {} } }""")));

    [Fact]
    public void StaleFetchedAt_StillParses_AgeIsCallersConcern()
    {
        var s = UsageCacheReader.TryRead(WriteFixture(
            """{ "cachedUsageUtilization": { "fetchedAtMs": 946684800000, "utilization": {} } }"""));
        Assert.NotNull(s);
        Assert.Equal(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), s!.FetchedAt);
        Assert.Null(s.FiveHour);
        Assert.Null(s.SevenDay);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(150)] // >100 is preserved; clamping is the renderer's job
    public void BoundaryPercentages_PreservedAsIs(int percent)
    {
        var s = UsageCacheReader.TryRead(WriteFixture($$"""
            { "cachedUsageUtilization": { "fetchedAtMs": 1784815176543,
              "utilization": { "five_hour": { "utilization": {{percent}}, "resets_at": "2026-07-23T18:39:59Z" } } } }
            """));
        Assert.Equal(percent, s!.FiveHour!.Percent);
        Assert.Null(s.SevenDay);
    }

    [Fact]
    public void WindowWithoutResetsAt_ParsesWithNullReset()
    {
        var s = UsageCacheReader.TryRead(WriteFixture(
            """{ "cachedUsageUtilization": { "fetchedAtMs": 1, "utilization": { "seven_day": { "utilization": 7 } } } }"""));
        Assert.Equal(7, s!.SevenDay!.Percent);
        Assert.Null(s.SevenDay.ResetsAt);
    }

    [Theory]
    [InlineData("9223372036854775807")]
    [InlineData("-9223372036854775808")]
    public void OutOfRangeFetchedAt_ReturnsNull(string fetchedAtMs)
        => Assert.Null(UsageCacheReader.TryRead(WriteFixture($$"""
            { "cachedUsageUtilization": { "fetchedAtMs": {{fetchedAtMs}}, "utilization": {} } }
            """)));

    [Fact]
    public void FractionalPercent_IsInvalidForThatWindow()
    {
        var s = UsageCacheReader.TryRead(WriteFixture(
            """{ "cachedUsageUtilization": { "fetchedAtMs": 1, "utilization": { "five_hour": { "utilization": 42.5 } } } }"""));
        Assert.NotNull(s);
        Assert.Null(s!.FiveHour);
    }

    [Fact]
    public void ConfigPath_Override_Wins()
        => Assert.Equal(@"C:\x\claude.json", ConfigPath.Resolve(@"C:\x\claude.json"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ConfigPath_Default_IsUserProfileClaudeJson(string? overridePath)
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
        Assert.Equal(expected, ConfigPath.Resolve(overridePath));
    }
}
