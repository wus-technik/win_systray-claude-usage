using ClaudeUsageTray.Core;
using ClaudeUsageTray.Tray;

namespace ClaudeUsageTray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var instance = SingleInstance.TryAcquire();
        if (instance is null) return;

        ApplicationConfiguration.Initialize();
        var settingsPath = Settings.DefaultPath;
        var settings = Settings.Load(settingsPath);
        Application.Run(new TrayApp(settings, settingsPath, isVelopackInstalled: false));
    }
}
