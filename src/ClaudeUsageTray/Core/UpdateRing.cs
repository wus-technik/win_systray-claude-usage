namespace ClaudeUsageTray.Core;

/// <summary>Which release ring one update check follows, as the three values Velopack needs: the
/// channel whose index to read, whether GitHub pre-releases count, and whether a lower version may
/// be installed. Pure and separate from <c>UpdateCheck</c> because it is the one decision that can
/// either strand beta users on a dead ring or push a beta at everyone.
///
/// Design and the verified Velopack behaviour behind it:
/// docs/superpowers/specs/2026-09-04-beta-release-ring-design.md.</summary>
/// <param name="Channel">Velopack channel, i.e. which <c>releases.{channel}.json</c> to read. Passed
/// as <c>UpdateOptions.ExplicitChannel</c>, always explicitly, so the ring the user asked for wins
/// over the channel baked into the installed package.</param>
/// <param name="IncludePrereleases">Whether the GitHub source may look at pre-releases. Beta packages
/// are uploaded with <c>--pre</c>, so this is what actually keeps them off stable installs — a second
/// barrier besides the channel, and the one that holds even if a channel index is misplaced.</param>
/// <param name="AllowVersionDowngrade">Whether Velopack may install a version lower than the running
/// one. Only ever true while a return to stable is pending; see <see cref="For"/>.</param>
public sealed record UpdateRing(string Channel, bool IncludePrereleases, bool AllowVersionDowngrade)
{
    /// <summary>`vpk pack` defaults to this on Windows, and every existing install records it in its
    /// manifest, so it cannot be renamed without orphaning them.</summary>
    public const string StableChannel = "win";

    public const string BetaChannel = "win-beta";

    /// <summary>Whether a channel name is the beta one. Also what an unrecorded setting follows, so
    /// the adoption rule and the ring rules cannot drift apart.</summary>
    public static bool IsBetaChannel(string? channel)
        => string.Equals(channel?.Trim(), BetaChannel, StringComparison.OrdinalIgnoreCase);

    /// <summary>The ring for one check.</summary>
    /// <param name="useBetaReleases">The user's setting, or null when they have never made the
    /// choice — a settings file written before the ring existed, a file with the key removed, or a
    /// fresh install with no file at all.</param>
    /// <param name="installedChannel">The channel recorded in the installed package's manifest
    /// (<c>VelopackLocator.Current.Channel</c>); null outside an install.</param>
    /// <remarks>Downgrading is enabled only for the switch back to stable, and that asymmetry is the
    /// point. Opting out has to clear the two cases Velopack guards behind the flag — the latest
    /// stable being older than the installed beta, and the same Major.Minor.Patch on the other
    /// channel — or the user would sit on a beta with no way home. Opting in never needs it: a beta
    /// is offered only when it is strictly newer, and because every stable release is mirrored into
    /// the beta channel, the beta ring is never behind the stable one. Enabling it there would let a
    /// lagging or partly published beta index pull an opted-in user backwards instead. It also
    /// self-heals: once the stable package is applied the manifest reads "win" again and this returns
    /// false, so no steady state ever permits a downgrade.
    ///
    /// With no recorded choice the installed channel decides, which is what stops the beta installer
    /// from undoing itself: a build installed from the beta Setup.exe has no setting yet, and reading
    /// that as "stable" would offer stable as a downgrade on its very first check. An explicit false
    /// is different from an absent one — that one does leave the ring.</remarks>
    public static UpdateRing For(bool? useBetaReleases, string? installedChannel)
    {
        var onBeta = IsBetaChannel(installedChannel);
        var useBeta = useBetaReleases ?? onBeta;
        return new UpdateRing(
            Channel: useBeta ? BetaChannel : StableChannel,
            IncludePrereleases: useBeta,
            AllowVersionDowngrade: !useBeta && onBeta);
    }
}
