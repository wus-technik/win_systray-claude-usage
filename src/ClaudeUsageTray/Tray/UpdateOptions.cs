using ClaudeUsageTray.Core;

namespace ClaudeUsageTray.Tray;

/// <summary>Everything the Settings dialog needs to show and act on the update state, as data and
/// delegates rather than a call into the static UpdateCheck — so the dialog can be driven without a
/// GitHub feed, an installed app, or a modal prompt.</summary>
/// <param name="InstalledVersion">The running version, already stripped of build metadata.</param>
/// <param name="IsInstalled">False for `dotnet run` and portable builds, which have no update path.</param>
/// <param name="InitialState">What is known before the user asks — a background check may already
/// have staged something.</param>
/// <param name="LatestVersion">The version behind <paramref name="InitialState"/>, when there is one.</param>
/// <param name="InitialReleaseNotes">The notes packed with that staged version, when there are any.</param>
/// <param name="CheckNow">Runs one check and reports what it found, including the notes Velopack
/// packed with the target release. Null notes are normal for packages built before the release
/// pipeline started passing them.</param>
/// <param name="Confirm">Asks the user to approve the restart, showing the release notes when the
/// package carried any. The first argument is the question, the second the notes.</param>
/// <param name="RestartToApply">Applies the staged update and relaunches. Does not return.</param>
public sealed record UpdateOptions(
    string InstalledVersion,
    bool IsInstalled,
    UpdateAvailability InitialState,
    string? LatestVersion,
    string? InitialReleaseNotes,
    Func<Task<(UpdateAvailability State, string? LatestVersion, string? ReleaseNotes)>> CheckNow,
    Func<string, string?, bool> Confirm,
    Action RestartToApply);
