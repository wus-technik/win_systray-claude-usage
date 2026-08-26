namespace ClaudeUsageTray.Core;

/// <summary>
/// Budget gate for status-page polls: a 30 s floor between attempts (so a manual "Refresh
/// now" cannot spam the public endpoint) and a 1/5/15-minute network-failure backoff, capped
/// at 15. Pure state machine driven by caller-supplied timestamps — no clocks, no threads,
/// fully unit-testable. Deliberately not FetchScheduler: its rolling-hour cap is tuned to the
/// Anthropic per-token budget and would block a 60/h poll.
/// </summary>
public sealed class StatusScheduler
{
    private static readonly TimeSpan Floor = TimeSpan.FromSeconds(30);

    private DateTimeOffset _notBefore = DateTimeOffset.MinValue;
    private int _failureStreak;

    public bool CanFetch(DateTimeOffset now) => now >= _notBefore;

    public void RecordAttempt(DateTimeOffset now) => _notBefore = now + Floor;

    public void RecordSuccess() => _failureStreak = 0;

    public void RecordFailure(DateTimeOffset now)
    {
        _failureStreak = Math.Min(_failureStreak + 1, 3);
        var minutes = _failureStreak switch { 1 => 1, 2 => 5, _ => 15 };
        _notBefore = now + TimeSpan.FromMinutes(minutes);
    }
}