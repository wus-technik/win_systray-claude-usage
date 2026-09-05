namespace ClaudeUsageTray.Core;

/// <summary>State of ~/.claude.json when the cache reader produced no snapshot. Unreadable means the
/// usage key is present but did not parse — the status is only consulted after TryRead returned null.</summary>
public enum ConfigStatus { Missing, NoUsageKey, Unreadable }

/// <summary>State of ~/.claude/.credentials.json. Unusable: the file exists but yields no valid token
/// (expired, near expiry, malformed).</summary>
public enum CredentialStatus { Missing, Unusable, Valid }

public sealed record NoDataFacts(ConfigStatus Config, CredentialStatus Credentials, DesktopHistoryStatus Desktop);

/// <summary>The one sentence shown when no source produced a snapshot. Names what is missing
/// instead of assuming Claude Code: on a desktop-only machine ~/.claude.json exists (the desktop
/// app installs its own Claude Code), so "run Claude Code" was never the right hint there.
/// Desktop facts come first because a found-but-empty file is the more specific one.</summary>
public static class NoDataReason
{
    public const string Default = "No usage data yet — open Claude Code or Claude Desktop.";

    public static string Describe(NoDataFacts f)
    {
        if (f.Desktop == DesktopHistoryStatus.NoSamples) return "Claude Desktop history found, but no samples yet.";
        if (f.Desktop == DesktopHistoryStatus.Unreadable) return "Claude Desktop history found, but it could not be read.";
        return f.Config switch
        {
            ConfigStatus.Missing => Default,
            ConfigStatus.NoUsageKey => f.Credentials switch
            {
                CredentialStatus.Missing =>
                    "Claude Code has not cached usage data, and there is no credentials file for a live fetch.",
                CredentialStatus.Unusable =>
                    "Claude Code has not cached usage data, and its credentials are not usable for a live fetch.",
                _ => "Claude Code has not cached usage data yet — waiting for the first live fetch.",
            },
            _ => "Claude Code's cached usage data could not be read.",
        };
    }
}
