using System.Globalization;

namespace ClaudeUsageTray.Core;

/// <summary>
/// The user's own statement of when their weekly limit resets, e.g. "Thu 03:00". Stated rather than
/// inferred: the desktop history's sd series contains genuine off-lattice counter restarts that a
/// "last drop wins" rule would have misread on 2 of 6 detections, and the user can read the real
/// value off Claude's own UI. A fabricated weekly reset is worse than none.
///
/// The zone is always a parameter, never TimeZoneInfo.Local read in here: Core is deliberately
/// ambient-free, and the DST cases cannot be tested against a function with no zone to vary.
/// </summary>
public sealed record WeeklyAnchor(DayOfWeek Day, TimeOnly TimeOfDay)
{
    private static readonly string[] DayNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    /// <summary>Weekday (three-letter abbreviation or full English name, any case) plus HH:mm.
    /// Null for anything else, including a valid-looking string with seconds or a 12-hour clock:
    /// the setting is round-tripped through Format, so accepting a shape we cannot re-emit would
    /// rewrite the user's file into something they never typed.</summary>
    public static WeeklyAnchor? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return null;

        // Enum.TryParse also accepts the numeric form, so "3 03:00" would parse and be rewritten to
        // "Wed 03:00" in the user's file — a shape they never typed.
        if (parts[0].Any(char.IsDigit)) return null;

        if (!Enum.TryParse<DayOfWeek>(parts[0], ignoreCase: true, out var day) || !Enum.IsDefined(day))
        {
            int index = Array.FindIndex(DayNames,
                n => string.Equals(n, parts[0], StringComparison.OrdinalIgnoreCase));
            if (index < 0) return null;
            day = (DayOfWeek)index;
        }

        // Exact formats only: "3:00" and "03:00" are the two the dialog and hand-editing produce.
        if (!TimeOnly.TryParseExact(parts[1], ["HH:mm", "H:mm"], CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var time)) return null;

        return new WeeklyAnchor(day, time);
    }

    /// <summary>The canonical spelling written back to settings.json, so "thu 3:00" and "Thursday
    /// 03:00" do not persist as two different-looking values for one anchor.</summary>
    public string Format() => $"{DayNames[(int)Day]} {TimeOfDay:HH\\:mm}";

    /// <summary>The next occurrence strictly after now, in the given zone. Strictly after, because
    /// an anchor landing exactly on now describes the reset that just happened, not the next one.</summary>
    public static DateTimeOffset NextReset(WeeklyAnchor anchor, DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        int ahead = ((int)anchor.Day - (int)local.DayOfWeek + 7) % 7;
        var day = local.Date.AddDays(ahead);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            var candidate = Resolve(day.Add(anchor.TimeOfDay.ToTimeSpan()), zone);
            if (candidate > now) return candidate;
            day = day.AddDays(7);   // this week's occurrence has passed, or fell exactly on now
        }
        // Unreachable: one week on is always in the future. Kept total rather than throwing, because
        // nothing on this path may throw.
        return Resolve(day.Add(anchor.TimeOfDay.ToTimeSpan()), zone);
    }

    /// <summary>A wall-clock instant in a zone, made total across both DST discontinuities. Spring
    /// forward: the wall time does not exist, so use the first instant after the gap. Fall back: it
    /// occurs twice, so use the earlier of the two, which is the one carrying the larger (pre-
    /// transition) offset.
    ///
    /// The gap is located by walking back a minute at a time rather than by reading
    /// TimeZoneInfo.GetAdjustmentRules: the walk is bounded by the gap's own length (an hour in
    /// every real zone), and First() over the rules throws InvalidOperationException when none
    /// matches — an exception DesktopUsageReader's catch filter does not list, on a path whose
    /// contract is that it never throws.</summary>
    private static DateTimeOffset Resolve(DateTime wall, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(unspecified))
        {
            // The first wall minute inside the gap, read with the offset still in force just before
            // it, IS the transition instant — i.e. the first instant after the gap. Adding the
            // delta to the *requested* time instead would land that far past the transition.
            var gapStart = unspecified;
            while (zone.IsInvalidTime(gapStart.AddMinutes(-1))) gapStart = gapStart.AddMinutes(-1);
            return new DateTimeOffset(gapStart, zone.GetUtcOffset(gapStart.AddMinutes(-1)));
        }

        if (zone.IsAmbiguousTime(unspecified))
            return new DateTimeOffset(unspecified, zone.GetAmbiguousTimeOffsets(unspecified).Max());

        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
    }
}
