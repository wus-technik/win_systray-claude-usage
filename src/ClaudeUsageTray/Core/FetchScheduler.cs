namespace ClaudeUsageTray.Core;

/// <summary>
/// Budget gate for usage-API fetches: a 30 s floor between attempts, a rolling-hour attempt
/// cap (manual and timed fetches combined), a proportionate rate-limit backoff, and
/// 5/10/20-minute network-failure backoff. Pure state machine driven by caller-supplied
/// timestamps — no clocks, no threads, fully unit-testable.
/// </summary>
public sealed class FetchScheduler
{
    private static readonly TimeSpan DefaultFloor = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _floor;

    /// <summary>
    /// Minimum wait after a 429. The endpoint's per-token budget is shared with Claude Code and
    /// recovers within ~90 s (it answers <c>Retry-After: 0</c>), so a flat multi-minute penalty
    /// would make the tray miss every brief window and show stale data indefinitely. A long
    /// <c>Retry-After</c> is still honored in full; the rolling-hour cap is the real abuse guard.
    /// </summary>
    private static readonly TimeSpan RateLimitFloor = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BudgetWindow = TimeSpan.FromHours(1);

    private readonly int _maxPerHour;
    private readonly Queue<DateTimeOffset> _attempts = new();
    private DateTimeOffset _notBefore = DateTimeOffset.MinValue;
    private int _failureStreak;

    /// <summary>Default 20/h with a 30 s floor: safety margin under Claude's measured budget.
    /// An unmeasured endpoint can supply a slower floor without sharing that budget.</summary>
    public FetchScheduler(int maxPerHour = 20, TimeSpan? floor = null)
    {
        _maxPerHour = maxPerHour;
        _floor = floor ?? DefaultFloor;
    }

    public bool CanFetch(DateTimeOffset now)
    {
        if (now < _notBefore) return false;
        while (_attempts.Count > 0 && now - _attempts.Peek() >= BudgetWindow) _attempts.Dequeue();
        return _attempts.Count < _maxPerHour;
    }

    public void RecordAttempt(DateTimeOffset now)
    {
        _attempts.Enqueue(now);
        _notBefore = now + _floor;
    }

    public void RecordSuccess() => _failureStreak = 0;

    public void RecordRateLimited(DateTimeOffset now, TimeSpan? retryAfter)
    {
        var delay = retryAfter is { } ra && ra > RateLimitFloor ? ra : RateLimitFloor;
        if (delay < _floor) delay = _floor;
        _notBefore = now + delay;
    }

    public void RecordFailure(DateTimeOffset now)
    {
        _failureStreak = Math.Min(_failureStreak + 1, 3);
        var minutes = _failureStreak switch { 1 => 5, 2 => 10, _ => 20 };
        var delay = TimeSpan.FromMinutes(minutes);
        _notBefore = now + (delay > _floor ? delay : _floor);
    }
}
