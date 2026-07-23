using Velopack;
using Velopack.Sources;

namespace ClaudeUsageTray;

public static class UpdateCheck
{
    // Public GitHub Releases feed; clients deliberately use no access token.
    private const string FeedUrl = "https://github.com/wus-technik/win_systray-claude-usage";
    private static readonly object Gate = new();
    private static UpdateManager? _manager;
    private static UpdateInfo? _stagedUpdate;

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
        get { lock (Gate) return _manager?.UpdatePendingRestart is not null || _stagedUpdate is not null; }
    }

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
