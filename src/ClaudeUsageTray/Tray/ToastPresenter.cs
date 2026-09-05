using System.Security;
using ClaudeUsageTray.Core;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
// Windows.UI.Notifications declares its own `Notification` type; ours is the Core record.
using Notification = ClaudeUsageTray.Core.Notification;

namespace ClaudeUsageTray.Tray;

/// <summary>
/// Shows a real Windows toast under Velopack's shortcut AUMID and routes a click to the popup.
/// Decides nothing: what to show, when, and with which tag is all NotificationRules' business.
///
/// Every WinRT call is wrapped. Failure is graduated: a notifier that cannot be created (the
/// unregistered-AUMID case of a dotnet run — measured to surface as either a COMException or a
/// silent no-op, so neither can be relied on) disables the presenter permanently; a failing Show()
/// disables it after three consecutive failures. Either way nothing throws and nothing else is
/// affected — the "nothing in the read paths throws" invariant applied to a write path.
///
/// Live toast objects are held until a terminal event so a click on a toast the GC collected cannot
/// silently do nothing; one per tag, and replacing a tag drops the previous reference rather than
/// waiting for a Dismissed a replacement may never deliver. Activated/Dismissed/Failed arrive off the
/// UI thread and are marshalled through the same BeginInvoke path TrayApp uses for fetch completions.
/// </summary>
public sealed class ToastPresenter : IDisposable
{
    public const string Group = "claudeusagetray";
    private const int MaxConsecutiveShowFailures = 3;

    private readonly string _aumid;
    private readonly Control _sync;
    private readonly Action _onActivated;
    private readonly Action<string> _log;
    private readonly ToastNotifier? _notifier;
    private readonly Dictionary<string, ToastNotification> _live = new(StringComparer.Ordinal);
    private int _consecutiveShowFailures;
    private bool _disabled;

    public ToastPresenter(string aumid, Control sync, Action onActivated, Action<string> log)
    {
        _aumid = aumid;
        _sync = sync;
        _onActivated = onActivated;
        _log = log;
        try
        {
            // Toasts persist across process exit. One left over from a previous process would
            // activate the shortcut on click, launch a second instance, and have SingleInstance exit
            // it — a click that appears to do nothing. Clearing first means no toast in Action Center
            // is ever older than the running process.
            ToastNotificationManager.History.Clear(aumid);
            _notifier = ToastNotificationManager.CreateToastNotifier(aumid);
            if (_notifier.Setting != NotificationSetting.Enabled)
                _log($"toast: disabled on the Windows side ({_notifier.Setting}); toasts will not show");
        }
        catch (Exception e)
        {
            _notifier = null;
            _disabled = true;
            _log($"toast: notifier unavailable ({e.GetType().Name}); notifications off for this session");
        }
    }

    public void Show(Notification notification)
    {
        if (_disabled || _notifier is null) return;
        try
        {
            var xml = new XmlDocument();
            xml.LoadXml(
                $"<toast launch=\"{Escape(notification.Argument)}\" activationType=\"foreground\">" +
                "<visual><binding template=\"ToastGeneric\">" +
                $"<text>{Escape(notification.Title)}</text>" +
                $"<text>{Escape(notification.Body)}</text>" +
                "</binding></visual></toast>");

            var toast = new ToastNotification(xml) { Group = Group, Tag = notification.Tag };
            if (notification.ExpiresAt is { } expires) toast.ExpirationTime = expires;

            var tag = notification.Tag;
            toast.Activated += (_, _) => Marshal(() => { Forget(tag, toast); _onActivated(); });
            // Dismissed(TimedOut) only means the banner slid into Action Center, where it can still be
            // clicked — so the object must stay referenced. Only the user closing it or the app hiding
            // it ends its life; a replacement under the same tag drops it via the dictionary anyway.
            toast.Dismissed += (_, e) => Marshal(() =>
            {
                if (e.Reason != ToastDismissalReason.TimedOut) Forget(tag, toast);
            });
            toast.Failed += (_, e) => Marshal(() =>
            {
                Forget(tag, toast);
                _log($"toast: show failed after display ({e.ErrorCode?.GetType().Name})");
            });

            _live[tag] = toast;   // replaces a predecessor's reference; Windows replaces it on screen
            _notifier.Show(toast);
            _consecutiveShowFailures = 0;
        }
        catch (Exception e)
        {
            _consecutiveShowFailures++;
            _log($"toast: show failed ({e.GetType().Name}), failure {_consecutiveShowFailures} of {MaxConsecutiveShowFailures}");
            if (_consecutiveShowFailures >= MaxConsecutiveShowFailures)
            {
                _disabled = true;
                _log("toast: three consecutive failures; notifications off for this session");
            }
        }
    }

    /// <summary>Retracts a toast whose claim is no longer true (a value that left red). The silent
    /// exit stays silent; it just stops leaving a false line in Action Center.
    /// Calling this for a tag that was never shown (e.g. NotificationRules asked to remove a toast
    /// that crossing happened while notifications were off) is a harmless no-op — do not "fix" it.</summary>
    public void Remove(string tag)
    {
        if (_notifier is null) return;
        try
        {
            _live.Remove(tag);
            ToastNotificationManager.History.Remove(tag, Group, _aumid);
        }
        catch (Exception e)
        {
            _log($"toast: remove failed ({e.GetType().Name})");
        }
    }

    private void Forget(string tag, ToastNotification toast)
    {
        if (_live.TryGetValue(tag, out var current) && ReferenceEquals(current, toast)) _live.Remove(tag);
    }

    private void Marshal(Action action)
    {
        try { _sync.BeginInvoke(action); }
        catch (InvalidOperationException) { /* app shutting down */ }
    }

    private static string Escape(string text) => SecurityElement.Escape(text) ?? "";

    public void Dispose() => _live.Clear();
}
