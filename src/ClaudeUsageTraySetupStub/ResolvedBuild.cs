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

    /// <summary>What is about to be installed, shown before the download. A decision, not
    /// string-building: the beta ring has two ways of ending up with stable content (no newer
    /// prerelease exists; the API was unavailable) and both must be said, or a user told "beta" who
    /// got stable has no reason to expect anything else.</summary>
    public string Describe()
    {
        if (Ring == Ring.Stable)
            return $"Installing the latest stable release of {Rings.ProductName}.";
        if (Via == ResolvedVia.LatestRedirect)
            return "GitHub's release list could not be read, so this installs the latest stable build on the beta ring. " +
                   "A newer pre-release may exist; the app's own update check will offer it once installed.";
        if (Version is { IsPrerelease: true })
            return $"Installing {Rings.ProductName} {Version} — a pre-release build on the beta ring.";
        return $"Installing {Rings.ProductName} {Version} on the beta ring. This is the current stable build — " +
               "no pre-release is newer than it. Pre-releases will be offered by the app as they appear.";
    }
}

/// <summary>The messages whose wording is a decision. Both switch texts describe *staging*: the app
/// checks on launch and downloads only, and applying is the user's explicit "Restart to update"
/// (UpdateCheck.cs). Telling a user "you are now on beta" while they still run stable would leave
/// them with no reason to look for that prompt.</summary>
public static class Wording
{
    public static string SwitchStaged(Ring target) => target == Ring.Beta
        ? $"Beta releases enabled. {Rings.ProductName} will download the next beta build in the background and " +
          "offer Restart to update when it is ready."
        : $"Beta releases disabled. {Rings.ProductName} will return to the latest stable build — which may be an " +
          "older version than the beta you are running — and offer Restart to update when it is ready.";

    public static string Installed(ResolvedBuild build)
    {
        var ring = build.Ring == Ring.Beta ? "beta ring" : "stable ring";
        var version = build.Version is null ? "" : $" {build.Version}";
        return $"{Rings.ProductName}{version} is installed on the {ring} and will keep itself up to date.";
    }
}
