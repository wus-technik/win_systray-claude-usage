namespace ClaudeUsageTray.Core;

/// <summary>
/// Best-effort rolling diagnostic log of usage-fetch outcomes. Every fetch skip, attempt, and
/// result is recorded so a "stale, never refreshes" report can be diagnosed from the user's
/// machine. Never throws — diagnostics must never break the tray. Capped at ~256 KiB with one
/// backup generation (fetch.log.1). Callers all run on the UI thread; a lock guards it anyway.
/// </summary>
public sealed class FetchLog
{
    private const long MaxBytes = 256 * 1024;
    private readonly string _path;
    private readonly object _gate = new();

    public FetchLog(string path) => _path = path;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeUsageTray", "fetch.log");

    /// <summary>Appends "<utc-iso8601> <message>" as one line. Silently no-ops on any IO error.</summary>
    public void Write(DateTimeOffset now, string message)
    {
        try
        {
            lock (_gate)
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                RotateIfNeeded();
                var line = $"{now.ToUniversalTime():yyyy-MM-ddTHH:mm:ss}Z {message}{Environment.NewLine}";
                File.AppendAllText(_path, line);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            // diagnostics must never break the tray
        }
    }

    private void RotateIfNeeded()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < MaxBytes) return;
        var backup = _path + ".1";
        if (File.Exists(backup)) File.Delete(backup);
        File.Move(_path, backup);
    }
}
