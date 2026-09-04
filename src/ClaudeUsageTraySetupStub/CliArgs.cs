namespace ClaudeUsageTraySetupStub;

/// <summary>What the command line asked for. <c>Ring</c> null means "not said" — interactively the
/// wizard asks, silently it is stable for a fresh install and an error against an existing one.</summary>
public sealed record StubOptions(Ring? Ring, bool Silent, string? Token, bool ShowVersion, bool ShowHelp);

public sealed record ParseResult(StubOptions? Options, string? Error);

public static class CliArgs
{
    public const string Usage = """
        ClaudeUsageTraySetup.exe [--ring stable|beta] [--silent] [--token <t>] [--version] [--help]

          --ring stable|beta   Install on, or switch an existing install to, this ring.
                               Required with --silent when the app is already installed.
          --silent             No wizard; passed through to Setup.exe. Always pass --ring too.
          --token <t>          GitHub token for the release lookup (also read from GH_TOKEN).
                               Raises the per-IP API rate limit for fleet rollouts of --ring beta.
          --version            Print this installer's version and build commit.
          --help               This text.

        Exit codes: 0 done or already so; Setup.exe's own code if it failed; 3001 bad arguments or
        SYSTEM context; 3002 release lookup failed; 3003 download or verification failed;
        3004 --silent without --ring on an existing install; 3005 could not stop/restart the app
        or write its settings; 3006 cancelled.
        """;

    public static ParseResult Parse(string[] args, string? environmentToken)
    {
        Ring? ring = null;
        var silent = false;
        string? token = null;
        var showVersion = false;
        var showHelp = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string? inlineValue = null;
            var equals = arg.IndexOf('=');
            if (arg.StartsWith("--", StringComparison.Ordinal) && equals > 0)
            {
                inlineValue = arg[(equals + 1)..];
                arg = arg[..equals];
            }

            switch (arg.ToLowerInvariant())
            {
                case "--ring":
                    var value = inlineValue ?? (i + 1 < args.Length ? args[++i] : null);
                    if (value is null) return Error("--ring needs a value: stable or beta.");
                    ring = value.ToLowerInvariant() switch
                    {
                        "stable" => Ring.Stable,
                        "beta" => Ring.Beta,
                        _ => null,
                    };
                    if (ring is null) return Error($"Unknown ring '{value}'. Use stable or beta.");
                    break;
                case "--silent":
                    silent = true;
                    break;
                case "--token":
                    token = inlineValue ?? (i + 1 < args.Length ? args[++i] : null);
                    if (string.IsNullOrWhiteSpace(token)) return Error("--token needs a value.");
                    break;
                case "--version":
                    showVersion = true;
                    break;
                case "--help" or "-h" or "/?":
                    showHelp = true;
                    break;
                default:
                    return Error($"Unknown argument '{arg}'.");
            }
        }

        if (token is null && !string.IsNullOrWhiteSpace(environmentToken)) token = environmentToken.Trim();
        return new ParseResult(new StubOptions(ring, silent, token, showVersion, showHelp), null);
    }

    private static ParseResult Error(string message) => new(null, message + Environment.NewLine + Environment.NewLine + Usage);
}
