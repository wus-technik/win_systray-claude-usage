namespace ClaudeUsageTraySetupStub;

/// <summary>How the build was found. The redirect carries no version and no digest, and for the beta
/// ring it means "the API was unavailable" — which the wizard has to say out loud.</summary>
public enum ResolvedVia { Api, LatestRedirect }

public sealed record ResolvedBuild(Ring Ring, string Channel, SemVer? Version, Uri Url, string? Digest, ResolvedVia Via)
{
    /// <summary>The `/releases/latest` redirect for the ring's channel: stable's only resolution, and
    /// beta's fallback. Content is the latest stable build either way.</summary>
    public static ResolvedBuild LatestOnChannel(Ring ring)
    {
        var channel = Rings.Channel(ring);
        return new ResolvedBuild(ring, channel, null, Rings.LatestAssetUrl(channel), null, ResolvedVia.LatestRedirect);
    }
}
