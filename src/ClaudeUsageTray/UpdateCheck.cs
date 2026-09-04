using System.Reflection;
using ClaudeUsageTray.Core;
using Velopack;
using Velopack.Sources;

namespace ClaudeUsageTray;

public static class UpdateCheck
{
    // Public GitHub Releases feed; clients deliberately use no access token.
    private const string FeedUrl = AppInfo.ProjectUrl;
    private static readonly object Gate = new();
    private static UpdateManager? _manager;
    private static UpdateInfo? _stagedUpdate;
    private static string? _latestKnownVersion;
    private static string? _latestKnownNotes;

    /// <summary>The running version. Velopack's own record when installed — it is the version the
    /// updater compares against — falling back to the assembly's for `dotnet run` and portable
    /// builds, where Velopack knows nothing about an install.</summary>
    public static string InstalledVersion
    {
        get
        {
            try
            {
                var manager = CreateManager();
                if (manager.IsInstalled && manager.CurrentVersion is { } version)
                    return VersionDisplay.Short(version.ToString());
            }
            catch { /* fall through to the assembly's own version */ }

            return VersionDisplay.Short(Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        }
    }

    /// <summary>The newest version the last check saw, so the UI can show something before the user
    /// asks for a fresh one. Null until a check has completed.</summary>
    public static string? LatestKnownVersion
    {
        get { lock (Gate) return _latestKnownVersion; }
    }

    /// <summary>The release notes Velopack packed with that version, so the dialog can show what a
    /// background check already staged. Null when the package carried none.</summary>
    public static string? LatestKnownReleaseNotes
    {
        get { lock (Gate) return _latestKnownNotes; }
    }

    public static bool IsInstalled
    {
        get
        {
            try { return CreateManager().IsInstalled; }
            catch { return false; }
        }
    }

    public static bool IsUpdateReady
    {
        get { lock (Gate) return IsUpdateReadyUnlocked(); }
    }

    private static bool IsUpdateReadyUnlocked()
        => _manager?.UpdatePendingRestart is not null || _stagedUpdate is not null;

    /// <summary>Check on launch and every 6 h; download only. Never terminate the tray process.</summary>
    public static async Task RunPeriodicAsync()
    {
        while (true)
        {
            try { await CheckOnceAsync(); }
            catch { /* update failures must never disturb the tray */ }
            await Task.Delay(TimeSpan.FromHours(6));
        }
    }

    /// <summary>One check on demand, for the Settings dialog's refresh button. Shares its body with the
    /// periodic check so there is a single definition of "check and stage", and reports the outcome
    /// instead of swallowing it — a user who pressed a button is owed an answer, unlike the background
    /// loop, whose failures must stay silent.</summary>
    public static async Task<(UpdateAvailability State, string? LatestVersion, string? ReleaseNotes)> CheckNowAsync()
    {
        try
        {
            if (!CreateManager().IsInstalled) return (UpdateAvailability.NotInstalled, null, null);
            await CheckOnceAsync();
        }
        catch
        {
            return (UpdateAvailability.Failed, null, null);
        }

        lock (Gate)
        {
            return IsUpdateReadyUnlocked()
                ? (UpdateAvailability.UpdateReady, _latestKnownVersion, _latestKnownNotes)
                : (UpdateAvailability.UpToDate, _latestKnownVersion, null);
        }
    }

    private static async Task CheckOnceAsync()
    {
        var manager = CreateManager();
        if (!manager.IsInstalled) return; // dev runs (dotnet run) are not updatable

        var updates = await manager.CheckForUpdatesAsync();
        if (updates is not null) await manager.DownloadUpdatesAsync(updates);

        lock (Gate)
        {
            _manager = manager;
            // Best-effort cache only; manager.UpdatePendingRestart (checked via IsUpdateReady/
            // RestartToApply) is the source of truth for whether an update is actually staged.
            _stagedUpdate = updates;
            // Null when nothing newer is offered. Only the UpdateReady wording names a version, so a
            // clear here cannot make a settled "up to date" look uncertain.
            _latestKnownVersion = updates?.TargetFullRelease?.Version?.ToString() is { } target
                ? VersionDisplay.Short(target)
                : null;
            // Whatever `vpk pack --releaseNotes` put on the target release. Packages built before the
            // pipeline passed it have none, which is not an error — the dialog then just asks plainly.
            _latestKnownNotes = ReleaseNotes.Format(updates?.TargetFullRelease?.NotesMarkdown);
        }
    }

    /// <summary>Explicit user action only: apply the staged package and relaunch.</summary>
    public static void RestartToApply()
    {
        lock (Gate)
        {
            if (_manager is { } manager && (manager.UpdatePendingRestart is not null || _stagedUpdate is not null))
            {
                // null is valid here: it applies an update staged in a previous session.
                manager.ApplyUpdatesAndRestart(_stagedUpdate!);
            }
        }
    }

    private static UpdateManager CreateManager()
        => new(new GithubSource(FeedUrl, accessToken: null, prerelease: false));
}
