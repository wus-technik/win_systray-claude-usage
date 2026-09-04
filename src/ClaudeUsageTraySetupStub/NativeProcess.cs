using System.Runtime.InteropServices;

namespace ClaudeUsageTraySetupStub;

/// <summary>A child that outlives the shell. Process.Start cannot set CREATE_BREAKAWAY_FROM_JOB, and
/// without it a child launched from a deployment agent's or terminal's job object dies with that job —
/// which looks exactly like a startup crash (CLAUDE.md documents the same trap for Update.exe apply).</summary>
internal sealed class StartedProcess(IntPtr handle, bool brokeAwayFromJob) : IDisposable
{
    public bool BrokeAwayFromJob { get; } = brokeAwayFromJob;

    /// <summary>Blocks until the child exits; -1 if the exit code could not be read.</summary>
    public int WaitForExit()
    {
        NativeProcess.WaitForSingleObject(handle, NativeProcess.Infinite);
        return NativeProcess.GetExitCodeProcess(handle, out var code) ? unchecked((int)code) : -1;
    }

    public void Dispose() => NativeProcess.CloseHandle(handle);
}

internal static unsafe class NativeProcess
{
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int ErrorAccessDenied = 5;
    internal const uint Infinite = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool CreateProcessW(char* applicationName, char* commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, char* currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetExitCodeProcess(IntPtr handle, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);

    /// <summary>Breakaway first; when the job forbids it (ERROR_ACCESS_DENIED) the child is started
    /// inside the job so the caller can at least wait for it and read its exit code. Null when even
    /// that fails.</summary>
    public static StartedProcess? Start(string exe, string arguments, bool tryBreakaway)
    {
        var commandLine = string.IsNullOrEmpty(arguments) ? $"\"{exe}\"" : $"\"{exe}\" {arguments}";
        if (tryBreakaway && TryCreate(exe, commandLine, CreateBreakawayFromJob, out var info))
            return new StartedProcess(info.hProcess, brokeAwayFromJob: true);
        if (tryBreakaway && Marshal.GetLastPInvokeError() != ErrorAccessDenied && Marshal.GetLastPInvokeError() != 0)
            return null;
        return TryCreate(exe, commandLine, 0, out info) ? new StartedProcess(info.hProcess, brokeAwayFromJob: false) : null;
    }

    private static bool TryCreate(string exe, string commandLine, uint extraFlags, out ProcessInformation info)
    {
        // CreateProcessW may write into the command-line buffer, so it must be a mutable copy.
        var buffer = (commandLine + '\0').ToCharArray();
        var application = (exe + '\0').ToCharArray();
        var startup = new StartupInfo { cb = (uint)sizeof(StartupInfo) };
        fixed (char* app = application)
        fixed (char* cmd = buffer)
        {
            var ok = CreateProcessW(app, cmd, IntPtr.Zero, IntPtr.Zero, false, extraFlags | CreateUnicodeEnvironment,
                IntPtr.Zero, null, ref startup, out info);
            if (ok) CloseHandle(info.hThread);
            return ok;
        }
    }
}
