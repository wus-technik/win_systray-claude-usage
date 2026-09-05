using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class DesktopUsageReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cut-desktop-reader-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string json, string name = "plan-usage-history.json")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, json);
        return path;
    }

    // Real shape, ascending, two samples; the newest has no xu.
    private const string Fixture = """
        {"version":2,"samples":[
          {"t":1785247200000,"org":"11111111-1111-1111-1111-111111111111","u":{"fh":63,"sd":29,"xu":66.68333333333332}},
          {"t":1785247500144,"org":"11111111-1111-1111-1111-111111111111","u":{"fh":64,"sd":29}}
        ]}
        """;

    [Fact]
    public void Fixture_NewestSampleBecomesTheSnapshot()
    {
        var r = DesktopUsageReader.Read(Write(Fixture));

        Assert.Equal(DesktopHistoryStatus.Ok, r.Status);
        var s = Assert.IsType<UsageSnapshot>(r.Snapshot);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1785247500144), s.FetchedAt);
        Assert.Equal(64, s.FiveHour!.Percent);
        Assert.Null(s.FiveHour.ResetsAt);
        Assert.Equal(29, s.SevenDay!.Percent);
        Assert.Null(s.SevenDay.ResetsAt);
        Assert.Null(s.Credits);
        Assert.Empty(s.ScopedLimits);
        Assert.Equal(UsageSource.DesktopHistory, s.Source);
    }

    [Fact]
    public void Descending_MaxByT_Wins()
    {
        var s = DesktopUsageReader.TryRead(Write("""
            {"version":2,"samples":[
              {"t":300,"org":"a","u":{"fh":30,"sd":3}},
              {"t":200,"org":"a","u":{"fh":20,"sd":2}},
              {"t":100,"org":"a","u":{"fh":10,"sd":1}}
            ]}
            """));
        Assert.Equal(30, s!.FiveHour!.Percent);
    }

    [Fact]
    public void Shuffled_MaxByT_Wins()
    {
        var s = DesktopUsageReader.TryRead(Write("""
            {"samples":[
              {"t":200,"u":{"fh":20,"sd":2}},
              {"t":300,"u":{"fh":30,"sd":3}},
              {"t":100,"u":{"fh":10,"sd":1}}
            ]}
            """));
        Assert.Equal(30, s!.FiveHour!.Percent);
    }

    [Fact]
    public void MultiOrg_NewestWinsEvenFromMinorityOrg()
    {
        var s = DesktopUsageReader.TryRead(Write("""
            {"samples":[
              {"t":100,"org":"majority","u":{"fh":10,"sd":1}},
              {"t":200,"org":"majority","u":{"fh":20,"sd":2}},
              {"t":300,"org":"minority","u":{"fh":77,"sd":7}}
            ]}
            """));
        Assert.Equal(77, s!.FiveHour!.Percent);
    }

    [Fact]
    public void Xu_BecomesPercentOnlyCredits()
    {
        var s = DesktopUsageReader.TryRead(Write("""{"samples":[{"t":1,"u":{"fh":1,"sd":1,"xu":66.68333333333332}}]}"""));
        var c = Assert.IsType<CreditUsage>(s!.Credits);
        Assert.Equal(67, c.Percent);
        Assert.Null(c.Used);
        Assert.Null(c.Limit);
        Assert.Null(c.PayloadSeverity);
        Assert.True(c.State.Enabled);
        Assert.False(c.State.LimitReached);
        Assert.Null(c.State.DisabledReason);
    }

    [Fact]
    public void Xu_Integer_IsAccepted()
    {
        var s = DesktopUsageReader.TryRead(Write("""{"samples":[{"t":1,"u":{"fh":1,"sd":1,"xu":100}}]}"""));
        Assert.Equal(100, s!.Credits!.Percent);
    }

    [Fact]
    public void MissingFh_LeavesFiveHourNull()
    {
        var s = DesktopUsageReader.TryRead(Write("""{"samples":[{"t":1,"u":{"sd":17}}]}"""));
        Assert.Null(s!.FiveHour);
        Assert.Equal(17, s.SevenDay!.Percent);
    }

    [Fact]
    public void BadSamplesAreSkipped_ValidSiblingStillSelected()
    {
        var s = DesktopUsageReader.TryRead(Write("""
            {"samples":[
              "not an object",
              {"u":{"fh":99,"sd":99}},
              {"t":"abc","u":{"fh":98,"sd":98}},
              {"t":99999999999999999999,"u":{"fh":97,"sd":97}},
              {"t":253402300800000,"u":{"fh":96,"sd":96}},
              {"t":500,"org":"a"},
              {"t":500,"org":"a","u":"nope"},
              {"t":400,"org":"a","u":{"fh":40,"sd":4}}
            ]}
            """));
        Assert.Equal(40, s!.FiveHour!.Percent);
    }

    [Fact]
    public void EmptySamples_IsNoSamples()
    {
        var r = DesktopUsageReader.Read(Write("""{"version":2,"samples":[]}"""));
        Assert.Null(r.Snapshot);
        Assert.Equal(DesktopHistoryStatus.NoSamples, r.Status);
    }

    [Fact]
    public void OnlyUnusableSamples_IsNoSamples()
        => Assert.Equal(DesktopHistoryStatus.NoSamples,
            DesktopUsageReader.Read(Write("""{"samples":[{"org":"a"},{"t":1}]}""")).Status);

    [Fact]
    public void SamplesNotAnArray_IsUnreadable()
        => Assert.Equal(DesktopHistoryStatus.Unreadable,
            DesktopUsageReader.Read(Write("""{"samples":{"t":1}}""")).Status);

    [Fact]
    public void NoSamplesKey_IsUnreadable()
        => Assert.Equal(DesktopHistoryStatus.Unreadable,
            DesktopUsageReader.Read(Write("""{"version":2}""")).Status);

    [Fact]
    public void MalformedJson_IsUnreadable()
        => Assert.Equal(DesktopHistoryStatus.Unreadable,
            DesktopUsageReader.Read(Write("{ not json !!")).Status);

    [Fact]
    public void MissingFile_IsNotFound()
    {
        var r = DesktopUsageReader.Read(Path.Combine(_dir, "nope.json"));
        Assert.Null(r.Snapshot);
        Assert.Equal(DesktopHistoryStatus.NotFound, r.Status);
    }

    [Fact]
    public void OversizeFile_IsUnreadable()
    {
        var path = Path.Combine(_dir, "big.json");
        using (var f = File.Create(path)) f.SetLength(16 * 1024 * 1024 + 1);
        Assert.Equal(DesktopHistoryStatus.Unreadable, DesktopUsageReader.Read(path).Status);
    }

    [Fact]
    public void UnknownVersion_StillParsed()
    {
        var s = DesktopUsageReader.TryRead(Write("""{"version":3,"samples":[{"t":1,"u":{"fh":5,"sd":6}}]}"""));
        Assert.Equal(5, s!.FiveHour!.Percent);
    }

    // ---- ReadFirst ----

    [Fact]
    public void ReadFirst_SkipsAMalformedNewerFile()
    {
        var broken = Write("{ half-written", "newer.json");
        var good = Write(Fixture, "older.json");
        var r = DesktopUsageReader.ReadFirst([broken, good]);
        Assert.Equal(DesktopHistoryStatus.Ok, r.Status);
        Assert.Equal(64, r.Snapshot!.FiveHour!.Percent);
    }

    [Fact]
    public void ReadFirst_AllFail_ReportsTheNewestExistingFilesStatus()
    {
        var empty = Write("""{"samples":[]}""", "newer.json");
        var broken = Write("{", "older.json");
        var r = DesktopUsageReader.ReadFirst([empty, broken]);
        Assert.Null(r.Snapshot);
        Assert.Equal(DesktopHistoryStatus.NoSamples, r.Status);
    }

    [Fact]
    public void ReadFirst_Empty_IsNotFound()
        => Assert.Equal(DesktopHistoryStatus.NotFound, DesktopUsageReader.ReadFirst([]).Status);

    [Fact]
    public void ReadFirst_OnlyMissingFiles_IsNotFound()
        => Assert.Equal(DesktopHistoryStatus.NotFound,
            DesktopUsageReader.ReadFirst([Path.Combine(_dir, "a.json"), Path.Combine(_dir, "b.json")]).Status);
}
