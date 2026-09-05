using System.Net;

namespace ClaudeUsageTraySetupStub;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var parsed = CliArgs.Parse(args, Environment.GetEnvironmentVariable("GH_TOKEN"));
        var log = new SetupLog(SetupLog.DefaultPath);
        if (parsed.Error is { } error)
        {
            log.Write($"exit {ExitCode.BadArguments}: bad arguments");
            if (!ConsoleOutput.TryWriteLine(error)) Wizard.Error("Bad arguments", error);
            return ExitCode.BadArguments;
        }

        var options = parsed.Options!;
        if (options.ShowHelp) return Print(CliArgs.Usage);
        if (options.ShowVersion) return Print($"ClaudeUsageTraySetup {StubVersion.Informational}");

        log.Write($"start: version={StubVersion.Informational} ring={options.Ring?.ToString() ?? "unset"} silent={options.Silent} token={(options.Token is null ? "no" : "yes")}");

        // Default system proxy so corporate proxies and TLS inspection work; a long total timeout because
        // the payload is a 58 MB installer on whatever link the user has.
        using var http = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(30),
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = true,
        })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };

        try
        {
            return new SetupRun(options, log, http).RunAsync().GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            // Last resort: an unexpected exception must still produce a readable exit, not a WER dialog.
            log.Write($"exit {ExitCode.AppControlFailed}: unhandled {e.GetType().Name}: {e.Message}");
            if (!options.Silent) Wizard.Error("Setup failed unexpectedly.", $"{e.GetType().Name}: {e.Message}\n\nDetails: {SetupLog.DefaultPath}");
            else ConsoleOutput.TryWriteLine($"Setup failed unexpectedly: {e.Message} (exit {ExitCode.AppControlFailed})");
            return ExitCode.AppControlFailed;
        }
    }

    private static int Print(string text)
    {
        if (!ConsoleOutput.TryWriteLine(text)) Wizard.Info(Wizard.Title, text);
        return ExitCode.Converged;
    }
}
