using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class CreditFormatTests
{
    private static CreditUsage Credits(Money? used, Money? limit, int percent,
        bool enabled = true, string? reason = null, bool limitReached = false)
        => new(used, limit, percent, null, new CreditState(enabled, reason, limitReached));

    [Fact]
    public void Describe_Money_UsesIsoCodeAndExponentDecimals()
        => Assert.Equal("40.01 / 40.00 EUR (100%)", CreditFormat.Describe(
            Credits(new Money(4001, "EUR", 2), new Money(4000, "EUR", 2), 100)));

    [Fact]
    public void Describe_ZeroExponent_RendersNoDecimals()
        => Assert.Equal("1500 / 2000 JPY (75%)", CreditFormat.Describe(
            Credits(new Money(1500, "JPY", 0), new Money(2000, "JPY", 0), 75)));

    [Fact]
    public void Describe_NoAmounts_RendersPercentOnly()
        => Assert.Equal("73%", CreditFormat.Describe(Credits(null, null, 73)));

    [Fact]
    public void Describe_OverLimit_KeepsThePercentAboveOneHundred()
        => Assert.Equal("50.00 / 40.00 EUR (125%)", CreditFormat.Describe(
            Credits(new Money(5000, "EUR", 2), new Money(4000, "EUR", 2), 125)));

    [Fact]
    public void Describe_MissingCurrencyCode_OmitsItRatherThanGuessing()
        => Assert.Equal("40.01 / 40.00 (100%)", CreditFormat.Describe(
            Credits(new Money(4001, "", 2), new Money(4000, "", 2), 100)));

    [Fact]
    public void DescribeState_Normal_IsNull()
        => Assert.Null(CreditFormat.DescribeState(new CreditState(true, null, false)));

    [Fact]
    public void DescribeState_LimitReached_SaysSo()
        => Assert.Equal("limit reached", CreditFormat.DescribeState(new CreditState(true, null, true)));

    [Fact]
    public void DescribeState_Disabled_IncludesTheHumanisedReason()
        => Assert.Equal("disabled — org spend cap reached",
            CreditFormat.DescribeState(new CreditState(false, "org_spend_cap_reached", false)));

    [Fact]
    public void DescribeState_DisabledWithoutReason_JustSaysDisabled()
        => Assert.Equal("disabled", CreditFormat.DescribeState(new CreditState(false, null, false)));
}
