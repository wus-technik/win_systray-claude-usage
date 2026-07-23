using Microsoft.Win32;

namespace ClaudeUsageTray.Tray;

/// <summary>Per-user run-at-login toggle. No admin rights required (HKCU only).</summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClaudeUsageTray";

    public static void Enable()
    {
        // Callers must first verify UpdateCheck.IsInstalled(); never register dotnet.exe from a dev run.
        if (string.IsNullOrWhiteSpace(Environment.ProcessPath))
            throw new InvalidOperationException("Installed executable path is unavailable.");
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is not null;
    }
}
