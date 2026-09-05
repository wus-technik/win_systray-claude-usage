using System.Text.Json;

namespace ClaudeUsageTray.Core;

/// <summary>Why a read of the desktop history produced no snapshot, for the no-data message.
/// Ok is the reader's own success marker.</summary>
public enum DesktopHistoryStatus { Ok, NotFound, Unreadable, NoSamples }

public sealed record DesktopHistoryResult(UsageSnapshot? Snapshot, DesktopHistoryStatus Status);

/// <summary>
/// Read-only parse of the Claude Desktop app's plan-usage-history.json. Field semantics are
/// inferred from observation on three machines, not documented: <c>u.fh</c> is the five-hour
/// utilization, <c>u.sd</c> the seven-day one, <c>u.xu</c> the extra-usage (credits) utilization,
/// present only while credits are enabled. There are no reset timestamps, so every window is
/// emitted with ResetsAt null and pace colouring falls back to the absolute thresholds.
/// The newest sample by <c>t</c> wins; array order and <c>org</c> are ignored (samples from a
/// second org appear after an org switch, and the newest is still the current one). A sample whose
/// <c>t</c> is further in the future than <see cref="SourceSelection.FutureTolerance"/> allows is
/// never selected: a single corrupt far-future timestamp would otherwise win max-by-t permanently
/// and block every later real sample, surviving a restart since it is re-read from the same file.
/// Never throws for IO/JSON errors.
/// </summary>
public static class DesktopUsageReader
{
    // The largest observed file is 172 KB; this guards against reading something else by mistake.
    private const long MaxBytes = 16 * 1024 * 1024;

    // DateTimeOffset.FromUnixTimeMilliseconds bounds; anything outside is not a timestamp.
    private const long MinUnixMs = -62_135_596_800_000;
    private const long MaxUnixMs = 253_402_300_799_999;

    public static UsageSnapshot? TryRead(string path, DateTimeOffset now) => Read(path, now).Snapshot;

    public static DesktopHistoryResult Read(string path, DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(path)) return new(null, DesktopHistoryStatus.NotFound);
            if (new FileInfo(path).Length > MaxBytes) return new(null, DesktopHistoryStatus.Unreadable);
            // FileShare.ReadWrite: the desktop app may be appending while we read.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(stream);

            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("samples", out var samples)
                || samples.ValueKind != JsonValueKind.Array)
                return new(null, DesktopHistoryStatus.Unreadable);

            // One pass, max-by-t. Ascending order has held on every machine measured, but nobody
            // guarantees it, and the pass costs nothing at a few thousand elements. A sample later
            // than now + FutureTolerance is never a candidate, however large its t.
            long futureCutoffMs = (now + SourceSelection.FutureTolerance).ToUnixTimeMilliseconds();
            JsonElement? newest = null;
            long newestT = long.MinValue;
            foreach (var sample in samples.EnumerateArray())
            {
                if (sample.ValueKind != JsonValueKind.Object) continue;
                if (!sample.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.Number
                    || !t.TryGetInt64(out var ms) || ms < MinUnixMs || ms > MaxUnixMs) continue;
                if (ms > futureCutoffMs) continue;
                if (!sample.TryGetProperty("u", out var u) || u.ValueKind != JsonValueKind.Object) continue;
                if (newest is null || ms > newestT) { newest = u; newestT = ms; }
            }
            if (newest is not { } usage) return new(null, DesktopHistoryStatus.NoSamples);

            var five = UsageJson.ReadRoundedPercent(usage, "fh") is { } fh ? new WindowUsage(fh, null) : null;
            var seven = UsageJson.ReadRoundedPercent(usage, "sd") is { } sd ? new WindowUsage(sd, null) : null;
            // Percent-only credits: no money, and no state the file could tell us about. This is the
            // same shape the legacy extra_usage block produces, which the credit row already renders.
            var credits = UsageJson.ReadRoundedPercent(usage, "xu") is { } xu
                ? new CreditUsage(null, null, xu, null, new CreditState(Enabled: true, null, LimitReached: false))
                : null;

            var snapshot = new UsageSnapshot(DateTimeOffset.FromUnixTimeMilliseconds(newestT), five, seven, [], credits)
            {
                Source = UsageSource.DesktopHistory,
            };
            return new(snapshot, DesktopHistoryStatus.Ok);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return new(null, DesktopHistoryStatus.Unreadable);
        }
    }

    /// <summary>Reads the candidates in the given order (newest first, see
    /// <see cref="DesktopHistoryPath.ByFreshness"/>) and returns the first usable snapshot, so a
    /// half-written newer file cannot mask an older good one. When none is usable, the status is the
    /// newest existing candidate's; NotFound when no candidate exists at all.</summary>
    public static DesktopHistoryResult ReadFirst(IReadOnlyList<string> byFreshness, DateTimeOffset now)
    {
        DesktopHistoryResult? firstFailure = null;
        foreach (var path in byFreshness)
        {
            var result = Read(path, now);
            if (result.Snapshot is not null) return result;
            if (result.Status != DesktopHistoryStatus.NotFound) firstFailure ??= result;
        }
        return firstFailure ?? new(null, DesktopHistoryStatus.NotFound);
    }
}
