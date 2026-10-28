namespace ClaudeUsageTray.Core;

/// <summary>Whether the Codex badges exist at all.</summary>
public enum CodexState { Unknown, Available, Unavailable }

/// <summary>
/// The Codex pair's existence, as a state machine rather than as conditions scattered through the
/// UI — the tray changing shape is the most visible thing this feature does.
///
/// The asymmetry is deliberate. Becoming available takes one snapshot; becoming unavailable takes a
/// *durable* signal, because <see cref="CodexCredentialsReader"/> returns null for any IO error,
/// including the transient lock while Codex itself rewrites the token. Without the streak the tray
/// would change shape during a routine token refresh. A failed *fetch* never removes anything: the
/// credentials are still there, the request merely failed, and that degrades to stale.
/// </summary>
public sealed class CodexAvailability
{
    /// <summary>Consecutive unreadable credential reads before giving up. Three poll cycles is far
    /// longer than any token rewrite and still short enough that a real logout is noticed.</summary>
    public const int FailuresBeforeUnavailable = 3;

    private int _unreadable;

    public CodexState State { get; private set; } = CodexState.Unknown;

    /// <summary>A usable reading arrived: available, and the failure streak is forgotten. Recovers
    /// from Unavailable, so logging back in restores the badges without a restart.</summary>
    public void NoteSnapshot()
    {
        _unreadable = 0;
        State = CodexState.Available;
    }

    /// <summary>auth.json is not there. Codex is not installed or is logged out — a durable answer,
    /// no streak required.</summary>
    public void NoteCredentialsMissing()
    {
        _unreadable = 0;
        State = CodexState.Unavailable;
    }

    /// <summary>auth.json is there but yielded nothing usable: expired, malformed, or locked.</summary>
    public void NoteCredentialsUnreadable()
    {
        if (++_unreadable >= FailuresBeforeUnavailable) State = CodexState.Unavailable;
    }

    /// <summary>A readable token breaks the credential-failure streak even if its HTTP request
    /// later fails. It does not establish availability until a snapshot arrives.</summary>
    public void NoteCredentialsReadable() => _unreadable = 0;

    /// <summary>A 401, a 429, or a dead network. Never changes the state: this is what dimming is
    /// for.</summary>
    public void NoteFetchFailure() { }
}
