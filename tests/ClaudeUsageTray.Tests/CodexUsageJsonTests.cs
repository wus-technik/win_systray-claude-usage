using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>Canned payloads only. Identifiers are placeholders: the real response carries an email,
/// a user id and an account id, and none of them belongs in a repository.</summary>
public class CodexUsageJsonTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 20, 0, 0, TimeSpan.Zero);

    /// <summary>The shape verified live on 2026-10-13, with identifiers replaced.</summary>
    private const string Full = """
        {
          "user_id": "user-TEST",
          "account_id": "00000000-0000-0000-0000-000000000000",
          "email": "test@example.com",
          "plan_type": "team",
          "rate_limit": {
            "allowed": true,
            "limit_reached": false,
            "primary_window":   { "used_percent": 42, "limit_window_seconds": 18000,  "reset_at": 1791932468 },
            "secondary_window": { "used_percent": 21, "limit_window_seconds": 604800, "reset_at": 1792474516 }
          },
          "credits": { "has_credits": true, "unlimited": false, "balance": null },
          "spend_control": {
            "reached": true,
            "individual_limit": {
              "source": "workspace_spend_controls", "unit": "credit",
              "limit": "5", "used": "5.573267102241516", "remaining": "0",
              "used_percent": 111, "remaining_percent": 0, "reset_at": 1793491200
            }
          }
        }
        """;

    [Fact]
    public void Parse_ReadsBothWindows()
    {
        var snapshot = CodexUsageJson.Parse(Full, Now);

        Assert.Equal(42, snapshot!.FiveHour!.Percent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791932468), snapshot.FiveHour.ResetsAt);
        Assert.Equal(21, snapshot.SevenDay!.Percent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792474516), snapshot.SevenDay.ResetsAt);
        Assert.Equal(UsageSource.Codex, snapshot.Source);
        Assert.Equal(Now, snapshot.FetchedAt);
    }

    [Fact]
    public void Parse_WeeklyOnlyPrimaryWindow_IsSevenDay()
    {
        var snapshot = CodexUsageJson.Parse("""
            { "rate_limit": {
                "primary_window": { "used_percent": 3, "limit_window_seconds": 604800, "reset_at": 1792474516 },
                "secondary_window": null
              } }
            """, Now);

        Assert.Null(snapshot!.FiveHour);
        Assert.Equal(3, snapshot.SevenDay!.Percent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792474516), snapshot.SevenDay.ResetsAt);
        Assert.Equal("7d", Assert.Single(UsageValues.Enumerate(snapshot, new Settings(), Now)).Key);
    }

    [Fact]
    public void Parse_ReversedWindows_MapByDuration()
    {
        var snapshot = CodexUsageJson.Parse("""
            { "rate_limit": {
                "primary_window": { "used_percent": 21, "limit_window_seconds": 604800 },
                "secondary_window": { "used_percent": 42, "limit_window_seconds": 18000 }
              } }
            """, Now);

        Assert.Equal(42, snapshot!.FiveHour!.Percent);
        Assert.Equal(21, snapshot.SevenDay!.Percent);
    }

    [Theory]
    [InlineData("86400")]
    [InlineData("0")]
    [InlineData("-18000")]
    [InlineData("null")]
    [InlineData("\"604800\"")]
    [InlineData("18000.5")]
    [InlineData("1e400")]
    public void Parse_UnsupportedOrMalformedDuration_DropsOnlyThatWindow(string duration)
    {
        var snapshot = CodexUsageJson.Parse($$"""
            { "rate_limit": {
                "primary_window": { "used_percent": 42, "limit_window_seconds": {{duration}} },
                "secondary_window": { "used_percent": 21, "limit_window_seconds": 604800 }
              } }
            """, Now);

        Assert.Null(snapshot!.FiveHour);
        Assert.Equal(21, snapshot.SevenDay!.Percent);
    }

    [Fact]
    public void Parse_ReadsSpendControlAsAMonthlyScopedLimit()
    {
        var limit = Assert.Single(CodexUsageJson.Parse(Full, Now)!.ScopedLimits);

        Assert.Equal("Spend control", limit.Label);
        Assert.Equal(111, limit.Percent);
        Assert.Equal(ScopedPeriod.Monthly, limit.Period);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793491200), limit.ResetsAt);
        Assert.True(limit.IsActive);
        Assert.Null(limit.ModelId);
    }

    /// <summary>Review Focus 3: the credits block says the account has credits and nothing about how
    /// many. A row here would be a green 0 % bar — the placeholder the invariants forbid.</summary>
    [Fact]
    public void Parse_NeverProducesACreditsRow()
        => Assert.Null(CodexUsageJson.Parse(Full, Now)!.Credits);

    /// <summary>Review Focus 2: absent blocks yield no rows, not zero rows — and a payload with no
    /// usable figure anywhere is not a snapshot at all. Adopting an empty one would reset the
    /// freshness clock and flip CodexAvailability to Available with nothing to show.</summary>
    [Fact]
    public void Parse_WithNoRateLimit_IsNull()
        => Assert.Null(CodexUsageJson.Parse("""{ "rate_limit": null, "plan_type": "team" }""", Now));

    /// <summary>One usable window is enough to be a snapshot; the other absent block yields no row
    /// rather than a zero row. This is the case that pins "absent means no row" — the all-absent
    /// case above pins "no reading at all".</summary>
    [Fact]
    public void Parse_WithOnlyOneWindow_IsASnapshotWithOneRow()
    {
        var snapshot = CodexUsageJson.Parse("""
            { "rate_limit": { "primary_window": { "used_percent": 7, "reset_at": 1791932468 } } }
            """, Now);

        Assert.NotNull(snapshot);
        Assert.Equal(7, snapshot!.FiveHour!.Percent);
        Assert.Null(snapshot.SevenDay);
        Assert.Empty(snapshot.ScopedLimits);
    }

    /// <summary>A reset_at outside DateTimeOffset's range must degrade, not throw:
    /// FromUnixTimeSeconds raises ArgumentOutOfRangeException, and a read path that throws takes the
    /// tray down.</summary>
    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void Parse_WithAnImpossibleReset_KeepsThePercentageAndDropsTheReset(long seconds)
    {
        var snapshot = CodexUsageJson.Parse($$"""
            { "rate_limit": { "primary_window": { "used_percent": 12, "reset_at": {{seconds}} } } }
            """, Now);

        Assert.Equal(12, snapshot!.FiveHour!.Percent);
        Assert.Null(snapshot.FiveHour.ResetsAt);
    }

    [Fact]
    public void Parse_WithSpendControlButNoIndividualLimit_HasNoScopedRow()
    {
        var snapshot = CodexUsageJson.Parse("""
            { "rate_limit": { "primary_window": { "used_percent": 5, "reset_at": 1791932468 } },
              "spend_control": { "reached": false, "individual_limit": null } }
            """, Now);

        Assert.Equal(5, snapshot!.FiveHour!.Percent);
        Assert.Empty(snapshot.ScopedLimits);
    }

    [Fact]
    public void Parse_WithOnlyTheSecondaryWindow_HasNoFiveHourRow()
    {
        var snapshot = CodexUsageJson.Parse("""
            { "rate_limit": { "secondary_window": { "used_percent": 9, "reset_at": 1792474516 } } }
            """, Now);

        Assert.Null(snapshot!.FiveHour);
        Assert.Equal(9, snapshot.SevenDay!.Percent);
    }

    /// <summary>A window with no reset is still a reading: the percentage is what the badge draws,
    /// and the countdown simply has nothing to say.</summary>
    [Fact]
    public void Parse_WindowWithoutAReset_KeepsThePercentage()
    {
        var snapshot = CodexUsageJson.Parse("""
            { "rate_limit": { "primary_window": { "used_percent": 33 } } }
            """, Now);

        Assert.Equal(33, snapshot!.FiveHour!.Percent);
        Assert.Null(snapshot.FiveHour.ResetsAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    public void Parse_Garbage_IsNull(string json)
        => Assert.Null(CodexUsageJson.Parse(json, Now));

    /// <summary>A payload with no usable figure at all is not a snapshot: adopting it would reset
    /// the freshness clock and make the display claim data it does not have.</summary>
    [Fact]
    public void Parse_EmptyObject_IsNull()
        => Assert.Null(CodexUsageJson.Parse("{}", Now));

    /// <summary>A percentage over 100 survives: spend control is already at 111 %, and clamping it
    /// here would hide a breached cap. The renderer clamps for drawing only.</summary>
    [Fact]
    public void Parse_KeepsAPercentageAboveOneHundred()
        => Assert.Equal(111, CodexUsageJson.Parse(Full, Now)!.ScopedLimits[0].Percent);
    [Theory]
    [InlineData("1e400")]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    public void Parse_UnrepresentablePercentage_DropsTheWindow(string percent)
        => Assert.Null(CodexUsageJson.Parse($$"""{ "rate_limit": { "primary_window": { "used_percent": {{percent}} } } }""", Now));

    [Fact]
    public void Parse_UnrepresentableSpend_DropsTheScopedRow()
    {
        var snapshot = CodexUsageJson.Parse("""
            { "rate_limit": { "primary_window": { "used_percent": 7 } },
              "spend_control": { "individual_limit": { "used_percent": 1e400 } } }
            """, Now);
        Assert.Empty(snapshot!.ScopedLimits);
    }
}
