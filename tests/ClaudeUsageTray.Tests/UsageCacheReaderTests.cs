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
    public void FractionalPercent_IsRoundedToInt()
    {
        // Claude Code writes the cache from the same API payload, which uses decimal utilization
        // (e.g. 42.5). Fractional values are valid and rounded — not treated as malformed.
        var s = UsageCacheReader.TryRead(WriteFixture(
            """{ "cachedUsageUtilization": { "fetchedAtMs": 1, "utilization": { "five_hour": { "utilization": 42.5 } } } }"""));
        Assert.NotNull(s);
        Assert.Equal(43, s!.FiveHour!.Percent);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("\"foo\"")]
    [InlineData("[1, 2, 3]")]
    [InlineData("null")]
    public void NonObjectRoot_ReturnsNull(string json)
        => Assert.Null(UsageCacheReader.TryRead(WriteFixture(json)));

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

    // ---- scoped limits (limits[]) ----

    private static string Wrap(string utilizationBody) => $$"""
        {
          "cachedUsageUtilization": {
            "fetchedAtMs": 1784815176543,
            "utilization": { {{utilizationBody}} }
          }
        }
        """;

    private static string Limit(string scope, int percent, bool isActive = false,
        string group = "weekly", string resetsAt = "2026-07-27T16:00:00Z") => $$"""
        { "group": "{{group}}", "percent": {{percent}}, "is_active": {{(isActive ? "true" : "false")}},
          "resets_at": "{{resetsAt}}", "scope": {{scope}} }
        """;

    private const string FableScope = """{ "model": { "id": null, "display_name": "Fable" }, "surface": null }""";

    [Fact]
    public void ScopedLimit_ModelScoped_IsParsed()
    {
        var s = UsageCacheReader.TryRead(WriteFixture(Wrap($"""
            "limits": [ {Limit(FableScope, 100, isActive: true)} ]
            """)));

        var limit = Assert.Single(s!.ScopedLimits);
        Assert.Equal("Fable", limit.Label);
        Assert.Null(limit.ModelId);
        Assert.Equal(100, limit.Percent);
        Assert.True(limit.IsActive);
        Assert.Equal(new DateTimeOffset(2026, 7, 27, 16, 0, 0, TimeSpan.Zero), limit.ResetsAt);
    }

    [Fact]
    public void ScopedLimits_NoLimitsKey_IsEmptyNotNull()
        => Assert.Empty(UsageCacheReader.TryRead(WriteFixture(ValidJson))!.ScopedLimits);

    [Fact]
    public void ScopedLimit_SessionGroup_IsExcluded()
        => Assert.Empty(UsageCacheReader.TryRead(WriteFixture(Wrap($"""
            "limits": [ {Limit(FableScope, 50, group: "session")} ]
            """)))!.ScopedLimits);

    [Fact]
    public void ScopedLimit_NullScope_IsExcluded()
        => Assert.Empty(UsageCacheReader.TryRead(WriteFixture(Wrap($"""
            "limits": [ {Limit("null", 90)} ]
            """)))!.ScopedLimits);

    [Fact]
    public void ScopedLimit_NoLabelDerivable_IsExcluded()
        => Assert.Empty(UsageCacheReader.TryRead(WriteFixture(Wrap(
            $"""
            "limits": [ {Limit("""{ "model": { "id": null, "display_name": null }, "surface": null }""", 70)} ]
            """)))!.ScopedLimits);

    [Fact]
    public void ScopedLimit_FallsBackToModelId_ForLabel()
    {
        var s = UsageCacheReader.TryRead(WriteFixture(Wrap(
            $"""
            "limits": [ {Limit("""{ "model": { "id": "claude-fable", "display_name": null } }""", 40)} ]
            """)));

        var limit = Assert.Single(s!.ScopedLimits);
        Assert.Equal("claude-fable", limit.Label);
        Assert.Equal("claude-fable", limit.ModelId);
    }

    [Fact]
    public void ScopedLimit_SurfaceOnly_IsIncludedWithUnderscoresAsSpaces()
    {
        var s = UsageCacheReader.TryRead(WriteFixture(Wrap(
            $"""
            "limits": [ {Limit("""{ "model": null, "surface": "claude_code" }""", 100, isActive: true)} ]
            """)));

        Assert.Equal("claude code", Assert.Single(s!.ScopedLimits).Label);
    }

    [Fact]
    public void ScopedLimit_ModelAndSurface_AreBothInTheLabel()
    {
        var s = UsageCacheReader.TryRead(WriteFixture(Wrap(
            $"""
            "limits": [ {Limit("""{ "model": { "display_name": "Fable" }, "surface": "claude_code" }""", 60)} ]
            """)));

        Assert.Equal("Fable (claude code)", Assert.Single(s!.ScopedLimits).Label);
    }

    [Fact]
    public void ScopedLimit_MalformedEntry_DoesNotDropItsSiblings()
    {
        var s = UsageCacheReader.TryRead(WriteFixture(Wrap(
            $$"""
            "limits": [
              { "group": "weekly", "percent": "not-a-number", "scope": {{FableScope}} },
              "a bare string",
              {{Limit("""{ "model": { "display_name": "Opus" } }""", 30)}}
            ]
            """)));

        Assert.Equal("Opus", Assert.Single(s!.ScopedLimits).Label);
    }

    [Fact]
    public void ScopedLimits_SameModelTwice_DedupesToHigherPercent()
    {
        var s = UsageCacheReader.TryRead(WriteFixture(Wrap(
            $$"""
            "limits": [
              {{Limit("""{ "model": { "id": null, "display_name": "Fable" } }""", 40)}},
              {{Limit("""{ "model": { "id": "claude-fable", "display_name": "Fable" } }""", 90, isActive: true)}}
            ]
            """)));

        var limit = Assert.Single(s!.ScopedLimits);
        Assert.Equal(90, limit.Percent);
        Assert.Equal("claude-fable", limit.ModelId);
        Assert.True(limit.IsActive);
    }

    [Fact]
    public void ScopedLimits_LabelsDifferingOnlyByCase_AreDeduped()
        => Assert.Single(UsageCacheReader.TryRead(WriteFixture(Wrap(
            $$"""
            "limits": [
              {{Limit("""{ "model": { "display_name": "Fable" } }""", 10)}},
              {{Limit("""{ "model": { "display_name": "fable" } }""", 20)}}
            ]
            """)))!.ScopedLimits);

    [Fact]
    public void ScopedLimits_ActiveSortsAheadOfHigherPercentInactive()
    {
        var s = UsageCacheReader.TryRead(WriteFixture(Wrap(
            $$"""
            "limits": [
              {{Limit("""{ "model": { "display_name": "Inactive" } }""", 100)}},
              {{Limit("""{ "model": { "display_name": "Active" } }""", 70, isActive: true)}}
            ]
            """)));

        Assert.Equal(new[] { "Active", "Inactive" }, s!.ScopedLimits.Select(l => l.Label));
    }

    // ---- credits (spend / extra_usage) ----

    private const string SpendBlock = """
        "spend": {
          "used":  { "amount_minor": 4001, "currency": "EUR", "exponent": 2 },
          "limit": { "amount_minor": 4000, "currency": "EUR", "exponent": 2 },
          "percent": 100, "severity": "critical", "enabled": true, "disabled_reason": null
        }
        """;

    private const string LegacyBlock = """
        "extra_usage": {
          "is_enabled": true, "monthly_limit": 4000, "used_credits": 4001, "utilization": 73,
          "currency": "EUR", "decimal_places": 2, "disabled_reason": null,
          "spend_limit_reached": false
        }
        """;

    [Fact]
    public void Credits_SpendBlock_IsParsedAsMoney()
    {
        var c = UsageCacheReader.TryRead(WriteFixture(Wrap(SpendBlock)))!.Credits;

        Assert.Equal(4001, c!.Used!.AmountMinor);
        Assert.Equal("EUR", c.Used.Currency);
        Assert.Equal(2, c.Used.Exponent);
        Assert.Equal(4000, c.Limit!.AmountMinor);
        Assert.Equal(100, c.Percent);
        Assert.Equal("critical", c.PayloadSeverity);
        Assert.True(c.State.Enabled);
    }

    [Fact]
    public void Credits_Absent_IsNull()
        => Assert.Null(UsageCacheReader.TryRead(WriteFixture(ValidJson))!.Credits);

    [Fact]
    public void Credits_OverLimit_ReportsLimitReachedFromSpendNotTheLegacyFlag()
    {
        // spend says 4001 of 4000; extra_usage.spend_limit_reached says false. spend must win.
        var c = UsageCacheReader.TryRead(WriteFixture(Wrap($"{SpendBlock}, {LegacyBlock}")))!.Credits;

        Assert.True(c!.State.LimitReached);
        Assert.Equal(100, c.Percent);          // from spend, not the legacy 73
    }

    [Fact]
    public void Credits_ZeroLimit_IsNotReportedAsLimitReached()
    {
        var c = UsageCacheReader.TryRead(WriteFixture(Wrap("""
            "spend": {
              "used":  { "amount_minor": 0, "currency": "EUR", "exponent": 2 },
              "limit": { "amount_minor": 0, "currency": "EUR", "exponent": 2 },
              "percent": 0, "enabled": false, "disabled_reason": "org_spend_cap_reached"
            }
            """)))!.Credits;

        Assert.False(c!.State.LimitReached);
        Assert.False(c.State.Enabled);
        Assert.Equal("org_spend_cap_reached", c.State.DisabledReason);
    }

    [Fact]
    public void Credits_LegacyOnly_YieldsPercentWithoutAmounts()
    {
        var c = UsageCacheReader.TryRead(WriteFixture(Wrap(LegacyBlock)))!.Credits;

        Assert.Null(c!.Used);
        Assert.Null(c.Limit);
        Assert.Equal(73, c.Percent);
        Assert.True(c.State.Enabled);
    }

    [Fact]
    public void Credits_SpendMissingAmounts_FallsBackToLegacy()
    {
        var c = UsageCacheReader.TryRead(WriteFixture(Wrap($$"""
            "spend": { "percent": 99, "enabled": true }, {{LegacyBlock}}
            """)))!.Credits;

        Assert.Null(c!.Used);
        Assert.Equal(73, c.Percent);   // legacy utilization, since spend had no money to trust
    }

    [Fact]
    public void Status_MissingFile_IsMissing()
        => Assert.Equal(ConfigStatus.Missing, UsageCacheReader.Status(Path.Combine(_dir, "nope.json")));

    [Fact]
    public void Status_FileWithoutTheKey_IsNoUsageKey()
        => Assert.Equal(ConfigStatus.NoUsageKey,
            UsageCacheReader.Status(WriteFixture("""{ "oauthAccount": {}, "userID": "x" }""")));

    [Fact]
    public void Status_FileWithTheKey_IsUnreadable()
    {
        // Status is only consulted when TryRead returned null, so "key present" means it did not parse.
        Assert.Equal(ConfigStatus.Unreadable,
            UsageCacheReader.Status(WriteFixture("""{ "cachedUsageUtilization": { "utilization": {} } }""")));
    }

    [Fact]
    public void Status_MalformedFileWithTheKey_IsUnreadable()
        => Assert.Equal(ConfigStatus.Unreadable,
            UsageCacheReader.Status(WriteFixture("""{ "cachedUsageUtilization": { not json""")));

    [Fact]
    public void Status_MalformedFileWithoutTheKey_IsNoUsageKey()
        => Assert.Equal(ConfigStatus.NoUsageKey, UsageCacheReader.Status(WriteFixture("{ not json !!")));

    // ---- chunked scan (Status must not ReadToEnd a multi-MB file) ----

    [Fact]
    public void Status_KeyBeyondFirstChunk_IsUnreadable()
    {
        // ~200 KB of filler before the key, well past the first 64 KiB chunk.
        var filler = new string('x', 150_000);
        var path = WriteFixture("{\"padding\":\"" + filler + "\",\"cachedUsageUtilization\":{}}");
        Assert.Equal(ConfigStatus.Unreadable, UsageCacheReader.Status(path));
    }

    [Fact]
    public void Status_KeySpanningChunkBoundary_IsUnreadable()
    {
        // Chunk size is 64 KiB (65536 chars). Place the filler so the needle starts 10 chars
        // before the boundary, i.e. straddles it: chars [65526, 65550) of the file are the needle.
        const int chunkSize = 64 * 1024;
        var filler = new string('x', chunkSize - 10);
        var path = WriteFixture("{" + filler + "\"cachedUsageUtilization\":{}}");
        Assert.Equal(ConfigStatus.Unreadable, UsageCacheReader.Status(path));
    }

    [Fact]
    public void Status_LargeFileWithoutKey_IsNoUsageKey()
    {
        var filler = new string('x', 200_000);
        var path = WriteFixture("{\"padding\":\"" + filler + "\",\"otherKey\":true}");
        Assert.Equal(ConfigStatus.NoUsageKey, UsageCacheReader.Status(path));
    }
}
