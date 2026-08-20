using System.Globalization;

namespace ClaudeUsageTray.Core;

public static class PaceFormat
{
    /// <summary>"1.4× pace" for a ratio, empty when there is none — pace did not decide the colour,
    /// so naming a number would explain something the user is not seeing. Invariant like the credit
    /// amounts: the UI text is English throughout, and a locale decimal comma next to it reads as a
    /// different quantity.</summary>
    public static string Describe(double? ratio)
        => ratio is { } r ? $"{r.ToString("0.0", CultureInfo.InvariantCulture)}× pace" : "";
}
