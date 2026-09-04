namespace ClaudeUsageTraySetupStub;

/// <summary>Process exit codes. Setup.exe's own non-zero code is propagated unchanged when it ran,
/// so the stub's failures sit in a range no installer uses. 0 means the requested state holds — also
/// when nothing had to change, so repeated runs with the same --ring are idempotent.</summary>
public static class ExitCode
{
    public const int Converged = 0;
    /// <summary>Bad arguments, or a SYSTEM / session-0 context where a per-user install is useless.</summary>
    public const int BadArguments = 3001;
    /// <summary>API unavailable with no usable fallback, or no release carries the channel asset.</summary>
    public const int ResolutionFailed = 3002;
    /// <summary>Download failed, or the file is empty, not a PE, or its digest does not match.</summary>
    public const int DownloadFailed = 3003;
    /// <summary>--silent without --ring against an existing install: the operator never said what
    /// the desired state is, so nothing can be treated as convergence.</summary>
    public const int AmbiguousRequest = 3004;
    /// <summary>Could not stop or relaunch the tray, or the settings write did not persist.</summary>
    public const int AppControlFailed = 3005;
    public const int Cancelled = 3006;
}
