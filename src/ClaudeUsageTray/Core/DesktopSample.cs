namespace ClaudeUsageTray.Core;

/// <summary>One eligible sample of the Claude Desktop history, already filtered by the reader's own
/// rules (object shape, timestamp bounds, future tolerance). The inference sees exactly the samples
/// the displayed percentages come from, so "the newest sample" means the same object in both.</summary>
public sealed record DesktopSample(DateTimeOffset At, string? Org, int? FiveHour, int? SevenDay);
