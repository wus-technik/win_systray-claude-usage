namespace ClaudeUsageTray.Core;

/// <summary>How one live usage fetch concluded, as far as arming cares. Relayed by TrayApp from the
/// outcomes it already distinguishes; it never decides anything about them.</summary>
public enum LiveOutcome { Snapshot, Unauthorized, RateLimited, Failed, NoToken }

/// <summary>What to show. Argument is the toast's activation payload; Tag is the Action Center slot
/// (a newer toast of a kind replaces its predecessor); ExpiresAt is when Action Center should stop
/// asserting it, null for the platform default.</summary>
public sealed record Notification(string Title, string Body, string Argument, string Tag, DateTimeOffset? ExpiresAt);

/// <summary>One evaluation's result. Log carries one line per decision event — never per tick — so
/// "I never got a toast" can be answered from fetch.log. RemoveUsageToast is set when a notified
/// usage value left the level, so the presenter can retract a claim that is no longer true.</summary>
public sealed record NotificationOutcome(Notification? Notification, IReadOnlyList<string> Log, bool RemoveUsageToast);

/// <summary>The Action Center slots and the activation payload, in one place because both rule
/// halves and the retraction site in TrayApp have to agree on them.</summary>
public static class NotificationTags
{
    public const string OpenPopupArgument = "open-popup";

    /// <summary>One usage slot per provider. A shared slot would make a second provider's toast
    /// replace the first's rather than sit beside it, and its retraction would pull the first's out
    /// of Action Center. Changing Claude's slot from the old bare "usage" orphans at most one toast
    /// that was on screen across the upgrade — it can no longer be retracted programmatically, and
    /// expires on its own.</summary>
    public static string Usage(string provider) => "usage:" + provider;

    public static string Status(string sourceId) => "status:" + sourceId;
}
