namespace ClaudeUsageTraySetupStub;

/// <summary>One run of the stub, start to exit code. Reports go to the log always, to the console in
/// silent mode, and to a dialog interactively.</summary>
internal sealed class SetupRun(StubOptions options, SetupLog log, HttpClient http)
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    public async Task<int> RunAsync()
    {
        if (ProcessControl.IsRefusedContext())
            return Fail(ExitCode.BadArguments, "Refusing to run as SYSTEM or in session 0.",
                $"A per-user install from that context lands in the SYSTEM profile and is useless. Run {Wizard.Title} in the user's own session.");

        var root = InstallPaths.DefaultRoot;
        var installed = InstallDetection.Detect(root, InstallDetection.ReadUninstallKeyVersion);
        var settingsPath = SettingsFile.DefaultPath;
        var settings = SettingsFile.Read(settingsPath);
        if (settings.Status is SettingsStatus.Malformed or SettingsStatus.WrongType)
            return Fail(ExitCode.AppControlFailed, "settings.json cannot be edited safely.",
                $"{settingsPath} is {(settings.Status == SettingsStatus.Malformed ? "not valid JSON" : $"carrying a non-boolean {SettingsEdit.Key}")}. " +
                "It was left untouched; fix or remove the file and run again.");

        Ring? currentRing = installed is null ? null : CurrentRing.Resolve(settings.UseBetaReleases, installed.Channel);
        log.Write(installed is null
            ? "state: not installed"
            : $"state: installed {installed.Version} channel={installed.Channel ?? "?"} setting={settings.UseBetaReleases?.ToString() ?? "null"} ring={currentRing}");

        if (installed is not null && ProcessControl.IsUpdateApplying(root))
            return Fail(ExitCode.AppControlFailed, "An update is being applied right now.", "Wait for it to finish and run this again.");

        if (installed is null && ProcessControl.IsTrayMutexHeld())
        {
            log.Write("warning: a tray outside the install tree is running (portable copy?)");
            if (!options.Silent)
                Wizard.Warning("A portable copy is running.",
                    $"A {Rings.ProductName} that was not installed by Setup is running. The installed app will share its settings with it.");
        }

        var effective = options;
        var decision = Flow.Decide(effective, installed, currentRing);
        if (decision.Step == Step.AskRing)
        {
            var chosen = Wizard.ChooseRing(decision.Ring, installed, currentRing);
            if (chosen is null) return Fail(ExitCode.Cancelled, "Cancelled.", null);
            effective = options with { Ring = chosen };
            decision = Flow.Decide(effective, installed, currentRing);
        }
        log.Write($"decision: {decision.Step} ring={decision.Ring}");

        return decision.Step switch
        {
            Step.Ambiguous => Fail(ExitCode.AmbiguousRequest,
                $"{Rings.ProductName} {installed!.Version} is installed on the {Name(currentRing!.Value)} ring.",
                "Pass --ring stable or --ring beta to say which ring it should be on; --silent alone changes nothing."),
            Step.Converged => Succeed($"{Rings.ProductName} {installed!.Version} is already on the {Name(decision.Ring)} ring.", null),
            Step.ChangeRing => ChangeRing(root, settingsPath, decision.Ring),
            _ => await InstallAsync(decision.Ring, settings, settingsPath, effective).ConfigureAwait(false),
        };
    }

    // ---- existing install: stop → write → relaunch ----

    private int ChangeRing(string root, string settingsPath, Ring target)
    {
        var (installedProcesses, _) = ProcessControl.FindTray(root);
        var wasRunning = installedProcesses.Count > 0;
        if (wasRunning && !ProcessControl.StopTray(installedProcesses, StopTimeout))
            return Fail(ExitCode.AppControlFailed, $"Could not stop {Rings.ProductName}.", "Nothing was changed. Quit it from the tray menu and run this again.");
        log.Write(wasRunning ? "tray: stopped" : "tray: was not running");

        var status = SettingsFile.Write(settingsPath, target == Ring.Beta);
        log.Write($"settings: write {SettingsEdit.Key}={target == Ring.Beta} -> {status}");
        if (status != SettingsWriteStatus.Written)
        {
            // The failure mode has to be "nothing changed" — never "installer ran, tray gone".
            if (wasRunning) ProcessControl.RelaunchTray(root);
            return Fail(ExitCode.AppControlFailed, "The setting could not be written.", $"{settingsPath}: {status}. Nothing was changed.");
        }

        if (wasRunning && !ProcessControl.RelaunchTray(root))
            return Fail(ExitCode.AppControlFailed, $"{Rings.ProductName} could not be restarted.",
                "The ring was changed, but the app is not running. Start it from the Start menu.");
        log.Write(wasRunning ? "tray: relaunched" : "tray: left stopped");
        return Succeed(Wording.SwitchStaged(target), null);
    }

    // ---- fresh install: resolve → download → verify → reconcile → Setup.exe ----

    private async Task<int> InstallAsync(Ring ring, SettingsReadResult settings, string settingsPath, StubOptions effective)
    {
        var resolve = await ReleaseResolver.ResolveAsync(http, ring, effective.Token, effective.Silent, HttpRetry.DefaultDelays, CancellationToken.None).ConfigureAwait(false);
        log.Write($"resolve: ring={ring} {resolve.Detail}");
        if (resolve.Build is null)
            return Fail(ExitCode.ResolutionFailed, "No installer could be found for the " + Name(ring) + " ring.", resolve.Detail);
        var build = resolve.Build;
        log.Write($"resolve: version={build.Version?.ToString() ?? "latest"} via={build.Via} url={build.Url}");

        var tempDir = Path.Combine(Path.GetTempPath(), $"ClaudeUsageTraySetup-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var setupPath = Path.Combine(tempDir, Rings.SetupAssetName(build.Channel));

            bool downloaded;
            if (effective.Silent)
            {
                downloaded = await Downloader.DownloadAsync(http, build.Url, setupPath, HttpRetry.DefaultDelays, null, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                var result = Wizard.RunWithProgress("Downloading", build.Describe(),
                    (progress, ct) => Downloader.DownloadAsync(http, build.Url, setupPath, HttpRetry.DefaultDelays, progress, ct));
                if (result is null) return Fail(ExitCode.Cancelled, "Cancelled.", null);
                downloaded = result.Value;
            }
            log.Write($"download: {(downloaded ? "ok" : "failed")}");
            if (!downloaded) return Fail(ExitCode.DownloadFailed, "The download failed.", build.Url.ToString());

            var verify = DownloadVerification.Verify(setupPath, build.Digest);
            log.Write($"verify: {verify} digest={(build.Digest is null ? "none" : "checked")}");
            if (verify != VerifyOutcome.Ok)
                return Fail(ExitCode.DownloadFailed, "The downloaded installer was rejected.", verify switch
                {
                    VerifyOutcome.Empty => "The file is empty.",
                    VerifyOutcome.NotExecutable => "The file is not a Windows executable.",
                    _ => "Its SHA-256 digest does not match what GitHub reports for the asset.",
                });

            // The stale-settings rule: a leftover explicit value contradicting the chosen ring would make
            // the very first update check undo this install.
            if (SettingsEdit.NeedsReconcile(settings.UseBetaReleases, ring))
            {
                var status = SettingsFile.Write(settingsPath, ring == Ring.Beta);
                log.Write($"settings: reconcile {SettingsEdit.Key}={ring == Ring.Beta} -> {status}");
                if (status != SettingsWriteStatus.Written)
                    return Fail(ExitCode.AppControlFailed, "A stale settings file could not be corrected.", $"{settingsPath}: {status}. Nothing was installed.");
            }

            // allowInJobFallback: true — this exit code must be read even when breakaway was denied.
            using var setup = NativeProcess.Start(setupPath, effective.Silent ? "--silent" : "", tryBreakaway: true, allowInJobFallback: true);
            if (setup is null) return Fail(ExitCode.AppControlFailed, "Setup.exe could not be started.", setupPath);
            if (!setup.BrokeAwayFromJob) log.Write("setup: breakaway from job denied; Setup.exe runs inside the caller's job");
            var code = setup.WaitForExit();
            log.Write($"setup: exit {code}");
            if (code < 0)
                return Fail(ExitCode.AppControlFailed, "Setup.exe finished but its exit code could not be read.", setupPath);
            if (code != 0)
                return Report(code, $"Setup.exe exited with code {code}.", "See %LOCALAPPDATA%\\WusTechnik.ClaudeUsageTray\\Velopack.log if it exists.", isError: true);

            return Succeed(Wording.Installed(build), build.Via == ResolvedVia.LatestRedirect && ring == Ring.Beta ? build.Describe() : null);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    // ---- reporting ----

    private static string Name(Ring ring) => ring == Ring.Beta ? "beta" : "stable";

    private int Succeed(string headline, string? detail) => Report(ExitCode.Converged, headline, detail, isError: false);

    private int Fail(int code, string headline, string? detail) => Report(code, headline, detail, isError: true);

    private int Report(int code, string headline, string? detail, bool isError)
    {
        log.Write($"exit {code}: {headline}{(detail is null ? "" : " " + detail)}");
        if (options.Silent)
        {
            ConsoleOutput.TryWriteLine($"{headline}{(detail is null ? "" : " " + detail)} (exit {code})");
        }
        else if (isError)
        {
            Wizard.Error(headline, detail ?? $"Exit code {code}. Details: {SetupLog.DefaultPath}");
        }
        else
        {
            Wizard.Info(headline, detail ?? "");
        }
        return code;
    }
}
