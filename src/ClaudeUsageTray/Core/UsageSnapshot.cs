namespace ClaudeUsageTray.Core;

/// <summary>Usage for one rolling window. Percent is the raw integer from the cache (may exceed 100).</summary>
public sealed record WindowUsage(int Percent, DateTimeOffset? ResetsAt);

/// <summary>The parsed cachedUsageUtilization payload. Windows are null when absent from the cache.</summary>
public sealed record UsageSnapshot(DateTimeOffset FetchedAt, WindowUsage? FiveHour, WindowUsage? SevenDay);
