namespace ClaudeUsageTray.Core;

public static class ConfigPath
{
    public static string Resolve(string? overridePath)
        => string.IsNullOrWhiteSpace(overridePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json")
            : overridePath;
}
