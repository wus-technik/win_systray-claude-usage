using ClaudeUsageTray.Core;
using ClaudeUsageTray.Tray;
using Velopack;

namespace ClaudeUsageTray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Velopack install/update/uninstall hooks — MUST run before anything else.
        VelopackApp.Build()
            .OnFirstRun(_ =>
            {
                if (Settings.Load(Settings.DefaultPath).RunAtStartup)
                    StartupRegistration.Enable();
            })
            .OnBeforeUninstallFastCallback(_ => StartupRegistration.Disable())
            .Run();

        using var instance = SingleInstance.TryAcquire();
        if (instance is null) return;

        ApplicationConfiguration.Initialize();
        var settingsPath = Settings.DefaultPath;
        var settings = Settings.Load(settingsPath);

        // Reconcile the HKCU Run key with the hand-editable runAtStartup setting on every
        // installed launch, so editing settings.json actually takes effect and drift self-heals.
        bool isInstalled = UpdateCheck.IsInstalled;
        if (isInstalled)
        {
            if (settings.RunAtStartup) StartupRegistration.Enable();
            else StartupRegistration.Disable();
        }

        _ = UpdateCheck.RunPeriodicAsync(); // fire-and-forget; never blocks the tray

        Application.Run(new TrayApp(settings, settingsPath, isInstalled));
    }
}
