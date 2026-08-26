using ClaudeUsageTray.Core;
using ClaudeUsageTray.Tray;

namespace ClaudeUsageTray.Tests;

internal static class TestUpdateOptions
{
    /// <summary>Update options that do nothing, for tests about the rest of the dialog. Checking and
    /// restarting throw rather than no-op, so a test that trips them fails loudly instead of quietly
    /// passing against a stub.</summary>
    public static UpdateOptions Inert() => new(
        InstalledVersion: "0.0.0-test",
        IsInstalled: false,
        InitialState: UpdateAvailability.NotInstalled,
        LatestVersion: null,
        InitialReleaseNotes: null,
        CheckNow: () => throw new InvalidOperationException("this test should not check for updates"),
        Confirm: (_, _) => throw new InvalidOperationException("this test should not prompt for a restart"),
        RestartToApply: () => throw new InvalidOperationException("this test should not restart"));
}
