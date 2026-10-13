namespace ClaudeUsageTraySetupStub;

/// <summary>Where the GitHub token came from, for the log and the warning. Pure: a command-line
/// token is readable in any process listing, so the supported path for fleet rollouts is GH_TOKEN
/// and an argument earns a warning. Never returns the token itself.</summary>
public static class TokenSource
{
    public const string CommandLineWarning =
        "Warning: --token puts the token on the command line, where any process listing on this " +
        "machine can read it. Set the GH_TOKEN environment variable instead.";

    public static string Describe(StubOptions options)
        => options.Token is null ? "none" : options.TokenOnCommandLine ? "argument" : "GH_TOKEN";
}
