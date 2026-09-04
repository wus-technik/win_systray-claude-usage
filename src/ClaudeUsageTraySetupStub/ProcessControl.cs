using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace ClaudeUsageTraySetupStub;

public static class ProcessControl
{
    /// <summary>A per-user install run as SYSTEM lands in the SYSTEM profile and exits 0 — silent,
    /// complete, useless. Intune Win32 apps and SCCM programs default to that context.</summary>
    public static bool MustRefuseContext(bool isLocalSystem, int sessionId) => isLocalSystem || sessionId == 0;

    public static bool IsRefusedContext()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var isSystem = identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true;
        return MustRefuseContext(isSystem, Process.GetCurrentProcess().SessionId);
    }

    public static bool IsInsideRoot(string? path, string root)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var prefix = Path.GetFullPath(root).TrimEnd('\\') + '\\';
        return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The single-instance mutex from SingleInstance.cs: held iff a tray is running in this
    /// session, installed or portable.</summary>
    public static bool IsTrayMutexHeld()
    {
        if (!Mutex.TryOpenExisting(InstallPaths.MutexName, out var mutex)) return false;
        mutex.Dispose();
        return true;
    }

    /// <summary>Tray processes split by whether they run from the install tree. A process whose path
    /// cannot be read (another user's, or gone already) counts as Other, never as Installed.</summary>
    public static (List<Process> Installed, List<Process> Other) FindTray(string root)
    {
        List<Process> installed = [], other = [];
        foreach (var process in Process.GetProcessesByName(InstallPaths.ExeName))
        {
            string? path = null;
            try { path = process.MainModule?.FileName; }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
            (IsInsideRoot(path, root) ? installed : other).Add(process);
        }
        return (installed, other);
    }

    /// <summary>An Update.exe from the install tree means the user just clicked Restart to update and
    /// current/ is about to be swapped. Killing the app now would race a directory being replaced.</summary>
    public static bool IsUpdateApplying(string root)
    {
        foreach (var process in Process.GetProcessesByName("Update"))
        {
            try
            {
                if (IsInsideRoot(process.MainModule?.FileName, root)) return true;
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
            finally { process.Dispose(); }
        }
        return false;
    }

    /// <summary>Terminate and wait. TrayApp is an ApplicationContext with no main window, so there is
    /// nothing to close politely. Waiting for the mutex to clear is not optional: a relaunch that
    /// races the dying process finds it held and exits silently, leaving no tray at all.</summary>
    public static bool StopTray(IReadOnlyList<Process> processes, TimeSpan timeout)
    {
        foreach (var process in processes)
        {
            try { process.Kill(); process.WaitForExit((int)timeout.TotalMilliseconds); }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
        }
        var deadline = DateTime.UtcNow + timeout;
        while (IsTrayMutexHeld())
        {
            if (DateTime.UtcNow > deadline) return false;
            Thread.Sleep(100);
        }
        return true;
    }

    /// <summary>Detached (breakaway from the caller's job). When breakaway is denied, the explorer.exe
    /// indirection CLAUDE.md uses: Explorer starts the child in its own job, so it survives the shell.</summary>
    public static bool RelaunchTray(string root)
    {
        var exe = InstallPaths.CurrentExe(root);
        if (!File.Exists(exe)) return false;
        using var started = NativeProcess.Start(exe, "", tryBreakaway: true);
        if (started is { BrokeAwayFromJob: true }) return true;
        try
        {
            using var explorer = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{exe}\"") { UseShellExecute = false });
            return explorer is not null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return started is not null; // inside the job is better than nothing
        }
    }
}
