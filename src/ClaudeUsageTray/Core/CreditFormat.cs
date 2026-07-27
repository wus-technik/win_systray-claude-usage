using System.Globalization;

namespace ClaudeUsageTray.Core;

/// <summary>Display strings for credit usage. Pure functions in Core so they are testable
/// without WinForms.</summary>
public static class CreditFormat
{
    /// <summary>"40.01 / 40.00 EUR (100%)", or "73%" when the amounts' units are unverified.
    /// The ISO code is used rather than a symbol: a code-to-symbol table is wrong for every code
    /// not in it, and CurrentCulture describes the user's locale, not the account's currency —
    /// it would print "$" for a EUR account.</summary>
    public static string Describe(CreditUsage c)
    {
        if (c.Used is not { } used || c.Limit is not { } limit) return $"{c.Percent}%";
        var code = string.IsNullOrEmpty(used.Currency) ? "" : $" {used.Currency}";
        return $"{Amount(used)} / {Amount(limit)}{code} ({c.Percent}%)";
    }

    /// <summary>The state worth showing on its own line, or null when there is none. Rendered
    /// separately rather than appended to the usage row, because "disabled" and "limit reached"
    /// mean different things and the reason carries which.</summary>
    public static string? DescribeState(CreditState s)
    {
        if (s.LimitReached) return "limit reached";
        if (s.Enabled) return null;
        return s.DisabledReason is { } reason ? $"disabled — {reason.Replace('_', ' ')}" : "disabled";
    }

    private static string Amount(Money m)
    {
        var scale = 1m;
        for (var i = 0; i < m.Exponent; i++) scale *= 10m;
        return (m.AmountMinor / scale).ToString($"F{m.Exponent}", CultureInfo.InvariantCulture);
    }
}
