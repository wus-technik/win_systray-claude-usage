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
                try
                {
                    if (Settings.Load(Settings.DefaultPath).RunAtStartup)
                        StartupRegistration.Enable();
                }
                catch { /* startup registration is best-effort; a locked-down registry must not kill the tray */ }
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
            try
            {
                if (settings.RunAtStartup) StartupRegistration.Enable();
                else StartupRegistration.Disable();
            }
            catch { /* startup registration is best-effort; a locked-down registry must not kill the tray */ }
        }

        // A file with no recorded choice adopts the channel this build was installed from, so a build
        // installed from the beta Setup.exe does not undo itself by treating "not chosen" as "stable".
        // Resolved here rather than left to the ring rules alone, so the Settings checkbox shows the
        // ring the app is actually on. Not written to disk until something else saves.
        settings.UseBetaReleases ??= UpdateRing.IsBetaChannel(UpdateCheck.InstalledChannel);

        // Before the first check, so the launch check already follows the ring the user picked.
        UpdateCheck.UseRing(settings.UseBetaReleases);
        _ = UpdateCheck.RunPeriodicAsync(); // fire-and-forget; never blocks the tray

        Application.Run(new TrayApp(settings, settingsPath, isInstalled));
    }
}
