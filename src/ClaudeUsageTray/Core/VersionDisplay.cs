namespace ClaudeUsageTray.Core;

/// <summary>What the update check currently knows. Distinct from "no update found": a check that has
/// not run yet must not claim the app is up to date.</summary>
public enum UpdateAvailability { Unknown, Checking, UpToDate, UpdateReady, Failed, NotInstalled }

/// <summary>Version and update-state wording, kept pure so the strings are testable without a
/// network, an installed app, or a form.</summary>
public static class VersionDisplay
{
    /// <summary>The version without its build metadata. The assembly's informational version carries
    /// the commit sha, which is for bug reports, not for a settings window.</summary>
    public static string Short(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return "unknown";
        var version = informationalVersion.Trim();
        var plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }

    /// <summary>The latest version is named only in the state where it means "this is what you would
    /// get" — printing it beside "up to date" would read as an update still waiting.</summary>
    public static string Describe(UpdateAvailability state, string? latestVersion) => state switch
    {
        UpdateAvailability.Checking => "checking…",
        UpdateAvailability.UpToDate => "up to date",
        UpdateAvailability.UpdateReady => string.IsNullOrWhiteSpace(latestVersion)
            ? "update ready to install"
            : $"{latestVersion.Trim()} ready to install",
        UpdateAvailability.Failed => "check failed",
        UpdateAvailability.NotInstalled => "updates are available only in the installed app",
        _ => "not checked yet",
    };

    /// <summary>Checking is blocked only while one is already in flight; every settled state, including
    /// a failure, may be retried. An uninstalled build has no feed to check against.</summary>
    public static bool CanCheck(UpdateAvailability state, bool isInstalled)
        => isInstalled && state != UpdateAvailability.Checking;
}
