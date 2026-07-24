namespace ClaudeUsageTray.Core;

/// <summary>
/// Budget gate for usage-API fetches: a 30 s floor between attempts, a rolling-hour attempt
/// cap (manual and timed fetches combined), a ≥ 15 min penalty on any rate limit, and
/// 5/10/20-minute network-failure backoff. Pure state machine driven by caller-supplied
/// timestamps — no clocks, no threads, fully unit-testable.
/// </summary>
public sealed class FetchScheduler
{
    private static readonly TimeSpan Floor = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RateLimitPenalty = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan BudgetWindow = TimeSpan.FromHours(1);

    private readonly int _maxPerHour;
    private readonly Queue<DateTimeOffset> _attempts = new();
    private DateTimeOffset _notBefore = DateTimeOffset.MinValue;
    private int _failureStreak;

    /// <summary>Default 20/h: safety margin under the endpoint's measured ~28-30/h per-token budget.</summary>
    public FetchScheduler(int maxPerHour = 20) => _maxPerHour = maxPerHour;

    public bool CanFetch(DateTimeOffset now)
    {
        if (now < _notBefore) return false;
        while (_attempts.Count > 0 && now - _attempts.Peek() >= BudgetWindow) _attempts.Dequeue();
        return _attempts.Count < _maxPerHour;
    }

    public void RecordAttempt(DateTimeOffset now)
    {
        _attempts.Enqueue(now);
        _notBefore = now + Floor;
    }

    public void RecordSuccess() => _failureStreak = 0;

    public void RecordRateLimited(DateTimeOffset now, TimeSpan? retryAfter)
        => _notBefore = now + (retryAfter > RateLimitPenalty ? retryAfter.Value : RateLimitPenalty);

    public void RecordFailure(DateTimeOffset now)
    {
        _failureStreak = Math.Min(_failureStreak + 1, 3);
        var minutes = _failureStreak switch { 1 => 5, 2 => 10, _ => 20 };
        _notBefore = now + TimeSpan.FromMinutes(minutes);
    }
}
