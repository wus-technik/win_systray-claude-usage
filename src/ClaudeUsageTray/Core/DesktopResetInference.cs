namespace ClaudeUsageTray.Core;

/// <summary>
/// Reconstructs the current 5-hour window's boundary from the Claude Desktop history, which carries
/// no reset field of any kind. A window started in the interval between two consecutive samples
/// whenever the newer one is above zero and either the older was zero or the value decreased; the
/// start is taken as the midpoint and the reset as start + 5 h.
///
/// The rule assumes every fh decrease is a boundary, which the file does not state. The evidence: the
/// 13 in-day windows reconstructed from these very decreases on a 30-day, 420-sample history came out
/// at 4.55–5.19 h. A decrease that was not a boundary would have produced a wildly non-5 h length, and
/// none did. The residual risk is self-limiting — a false start is one bad badge colour, re-evaluated
/// on the next tick, not a persistent state.
///
/// Every gate means emit nothing, never emit something weaker. The common case on a fresh install is
/// gate 4, and it must be quiet rather than an error.
/// </summary>
public static class DesktopResetInference
{
    /// <summary>The median sample gap is 15 min, so a bracket this wide is the measured ±8 min
    /// accuracy — 2.7 % of the period. Anything wider is not a boundary observation, it is a guess
    /// about a gap.</summary>
    public static readonly TimeSpan MaxBracket = TimeSpan.FromMinutes(20);

    /// <summary>Only the newest this many samples by timestamp participate, so the pair walk on a
    /// pathological file stays bounded. By timestamp, not by array position: ascending order has
    /// held on every machine measured, but nothing guarantees it, and capping the wrong end would
    /// silently discard exactly the samples the boundary is in.</summary>
    public const int MaxSamples = 20_000;

    /// <param name="org">The newest eligible sample's org, supplied by the reader rather than
    /// re-derived here, so the two cannot disagree about which sample is newest.</param>
    public static DateTimeOffset? FiveHourReset(IReadOnlyList<DesktopSample> samples, string? org,
        DateTimeOffset now)
    {
        if (samples.Count < 2) return null;

        // Sort defensively, then keep the newest MaxSamples — the tail of the sorted series, which
        // is what "by t, not by array position" means. The sort itself is unavoidable since order is
        // not guaranteed; the cap is what bounds the walk below.
        var sorted = samples.OrderBy(s => s.At).ToList();
        int first = Math.Max(0, sorted.Count - MaxSamples);

        // Gate 3 — no window is running, and pace at 0 % is meaningless anyway.
        if (sorted[^1].FiveHour is not > 0) return null;

        // Contiguous run, not a filter: a plain "keep samples matching the newest org" would pair two
        // same-org samples across an excluded other-org sample, and read a drop this design claims to
        // exclude as a boundary with a bracket narrow enough to pass gate 1. A missing org is its own
        // value and breaks a run like any other change.
        int runStart = sorted.Count - 1;
        while (runStart > first && sorted[runStart - 1].Org == org) runStart--;

        // The *last* qualifying pair, taken unconditionally — gate 1 then judges that pair alone.
        // Falling back to an earlier, narrower bracket when the newest boundary is poorly observed
        // would emit a reset for a window that has already turned over.
        (DesktopSample Previous, DesktopSample Current)? last = null;
        for (int i = runStart + 1; i < sorted.Count; i++)
        {
            var previous = sorted[i - 1];
            var current = sorted[i];
            if (current.FiveHour is not { } fh || fh <= 0) continue;
            // The second clause covers a user working straight through a boundary, where fh never
            // touches 0.
            if (previous.FiveHour is not { } before || !(before == 0 || fh < before)) continue;
            last = (previous, current);
        }

        // Gate 4 — history opens mid-window, or the only start sits behind a long gap.
        if (last is not { } pair) return null;

        var width = pair.Current.At - pair.Previous.At;
        // Gate 1 — a wider interval is not a boundary observation, it is a guess about a gap.
        if (width > MaxBracket) return null;

        var resetsAt = pair.Previous.At + width / 2 + UsageValues.FiveHourPeriod;
        // Gate 2 — an expired window says nothing about whatever may have started since.
        return now < resetsAt ? resetsAt : null;
    }
}
