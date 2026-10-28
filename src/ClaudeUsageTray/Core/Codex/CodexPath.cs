using System.Security;

namespace ClaudeUsageTray.Core;

/// <summary>Where Codex keeps its state. Mirrors <see cref="ConfigPath.Resolve"/>, with one extra
/// rule: the Codex CLI honours CODEX_HOME, so a relocated home has to be followed or the tray reads
/// a token from a directory Codex has abandoned. The caller passes the override rather than this
/// reading the environment itself, so the resolution is testable without mutating process state.</summary>
public static class CodexPath
{
    public const string HomeVariable = "CODEX_HOME";

    public static string Home(string? overrideHome = null)
        => string.IsNullOrWhiteSpace(overrideHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : overrideHome;

    public static string AuthFile(string? overrideHome = null)
        => Path.Combine(Home(overrideHome), "auth.json");

    /// <summary>The override the app actually runs with: CODEX_HOME when the environment sets it.
    /// Separated from <see cref="Home"/> so every other caller stays pure.</summary>
    public static string? HomeFromEnvironment()
    {
        try { return Environment.GetEnvironmentVariable(HomeVariable); }
        catch (SecurityException) { return null; }
    }
}
