using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>Whether the Codex badges exist at all. The tray changing shape is the most visible thing
/// this feature does, so every transition is pinned.</summary>
public class CodexAvailabilityTests
{
    [Fact]
    public void StartsUnknown()
        => Assert.Equal(CodexState.Unknown, new CodexAvailability().State);

    [Fact]
    public void ASnapshotMakesItAvailable()
    {
        var availability = new CodexAvailability();
        availability.NoteSnapshot();
        Assert.Equal(CodexState.Available, availability.State);
    }

    /// <summary>An absent auth.json is a durable answer: Codex is not installed or is logged out.</summary>
    [Fact]
    public void MissingCredentialsAreImmediatelyUnavailable()
    {
        var availability = new CodexAvailability();
        availability.NoteCredentialsMissing();
        Assert.Equal(CodexState.Unavailable, availability.State);
    }

    /// <summary>Review Focus 4: CodexCredentialsReader returns null for any IO error, including the
    /// transient lock while Codex rewrites the token. Two failures must not change the tray's shape.</summary>
    [Fact]
    public void TwoUnreadableAttemptsKeepItAvailable()
    {
        var availability = new CodexAvailability();
        availability.NoteSnapshot();

        availability.NoteCredentialsUnreadable();
        availability.NoteCredentialsUnreadable();

        Assert.Equal(CodexState.Available, availability.State);
    }

    [Fact]
    public void ThreeUnreadableAttemptsGiveUp()
    {
        var availability = new CodexAvailability();
        availability.NoteSnapshot();

        availability.NoteCredentialsUnreadable();
        availability.NoteCredentialsUnreadable();
        availability.NoteCredentialsUnreadable();

        Assert.Equal(CodexState.Unavailable, availability.State);
    }

    [Fact]
    public void AGoodReadResetsTheStreak()
    {
        var availability = new CodexAvailability();
        availability.NoteSnapshot();

        availability.NoteCredentialsUnreadable();
        availability.NoteCredentialsUnreadable();
        availability.NoteSnapshot();
        availability.NoteCredentialsUnreadable();

        Assert.Equal(CodexState.Available, availability.State);
    }

    /// <summary>A 401, a 429 or a dead network dims the icons; it never removes them. The
    /// credentials are still there — the request failed, which is a different claim.</summary>
    [Fact]
    public void FetchFailuresNeverRemoveTheIcons()
    {
        var availability = new CodexAvailability();
        availability.NoteSnapshot();

        for (int i = 0; i < 10; i++) availability.NoteFetchFailure();

        Assert.Equal(CodexState.Available, availability.State);
    }

    /// <summary>Recovery is possible: a user who logs back in gets the badges back without a
    /// restart.</summary>
    [Fact]
    public void ASnapshotAfterUnavailableRecovers()
    {
        var availability = new CodexAvailability();
        availability.NoteCredentialsMissing();
        availability.NoteSnapshot();

        Assert.Equal(CodexState.Available, availability.State);
    }
    [Fact]
    public void AReadableTokenBreaksTheUnreadableStreakEvenWhenTheFetchFails()
    {
        var availability = new CodexAvailability();
        availability.NoteSnapshot();
        availability.NoteCredentialsUnreadable();
        availability.NoteCredentialsUnreadable();
        availability.NoteCredentialsReadable();
        availability.NoteFetchFailure();
        availability.NoteCredentialsUnreadable();
        Assert.Equal(CodexState.Available, availability.State);
    }
}
