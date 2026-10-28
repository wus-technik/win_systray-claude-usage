namespace ClaudeUsageTray.Core;

/// <summary>Which of a provider's two badges should exist.</summary>
public sealed record WantedIcons(bool Five, bool Seven);

/// <summary>Which tray icons each provider contributes. A pure function rather than two conditions
/// inside ApplyDisplayMode, because "the Codex badges appear only once there is data" is a rule worth
/// a test, and TrayApp is not constructible in one.</summary>
public static class IconPresence
{
    /// <summary>Claude's pair follows the display mode alone: the icons exist from startup and carry
    /// the no-data badge until a reading arrives. Claude is the app's reason to be in the tray, and
    /// an empty tray would read as a crash.</summary>
    public static WantedIcons ForClaude(DisplayMode mode)
        => new(mode is DisplayMode.FiveHour or DisplayMode.Both,
               mode is DisplayMode.SevenDay or DisplayMode.Both);

    /// <summary>Codex's pair needs data as well as the setting. There is no enable toggle, so
    /// presence is the signal — and a badge for a tool that is not installed would be noise.</summary>
    public static WantedIcons ForCodex(CodexState state, DisplayMode mode)
        => state != CodexState.Available ? new(false, false) : ForClaude(mode);
}
