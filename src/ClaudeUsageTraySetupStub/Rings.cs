using ClaudeUsageTray.Core;

namespace ClaudeUsageTraySetupStub;

public enum Ring { Stable, Beta }

/// <summary>Ring ↔ Velopack channel, and everything derived from the channel string. Kept on top of
/// the linked <see cref="UpdateRing"/> constants so a renamed channel cannot desynchronise the app
/// and the installer.</summary>
public static class Rings
{
    public const string Repository = "wus-technik/win_systray-claude-usage";
    public const string ProductName = "Claude Usage Tray";

    public static string Channel(Ring ring)
        => ring == Ring.Beta ? UpdateRing.BetaChannel : UpdateRing.StableChannel;

    public static Ring FromChannel(string? channel)
        => UpdateRing.IsBetaChannel(channel) ? Ring.Beta : Ring.Stable;

    /// <summary>`vpk pack --channel X` names its installer `{packId}-{X}-Setup.exe`.</summary>
    public static string SetupAssetName(string channel) => $"WusTechnik.ClaudeUsageTray-{channel}-Setup.exe";

    /// <summary>The redirect GitHub keeps pointing at the newest non-prerelease release. Stable's whole
    /// resolution; beta's fallback when the API is unavailable (the win-beta mirror exists on every
    /// stable release, which is the third reason that mirror is mandatory).</summary>
    public static Uri LatestAssetUrl(string channel)
        => new($"https://github.com/{Repository}/releases/latest/download/{SetupAssetName(channel)}");
}
