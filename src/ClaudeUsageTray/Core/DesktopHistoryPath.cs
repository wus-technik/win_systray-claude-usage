namespace ClaudeUsageTray.Core;

/// <summary>Where the Claude Desktop app keeps plan-usage-history.json. Two locations exist in the
/// wild and the install kind does not predict which: the same MSIX package family wrote to the
/// classic %APPDATA% on one version and to its package container on later ones. Both are probed
/// unconditionally and the caller reads them newest-first. Finding the file is the only evidence
/// that the desktop app is present — %APPDATA%\Claude existing proves nothing (it can be a
/// hand-placed config, or an orphaned profile that is still touched).</summary>
public static class DesktopHistoryPath
{
    public const string FileName = "plan-usage-history.json";

    public static string DefaultAppData
        => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static string DefaultLocalAppData
        => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>The paths worth probing, existence not checked. With an override, only that path;
    /// an override that is not even a valid path yields nothing rather than throwing.</summary>
    public static IReadOnlyList<string> Candidates(string? overridePath, string appData, string localAppData)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
            return IsValidPath(overridePath) ? [overridePath] : [];

        var list = new List<string>();
        Guard(() => list.Add(Path.Combine(appData, "Claude", FileName)));
        Guard(() =>
        {
            var packages = Path.Combine(localAppData, "Packages");
            if (!Directory.Exists(packages)) return;
            // Glob the package family: the publisher hash is stable in practice but not guaranteed.
            foreach (var dir in Directory.EnumerateDirectories(packages, "Claude_*"))
                list.Add(Path.Combine(dir, "LocalCache", "Roaming", "Claude", FileName));
        });
        return list;
    }

    /// <summary>The existing candidates, newest LastWriteTimeUtc first. Keyed on the usage file's
    /// own write time, never its directory's. A candidate whose probe fails is dropped; the others
    /// survive. Never throws.</summary>
    public static IReadOnlyList<string> ByFreshness(IEnumerable<string> candidates)
    {
        var existing = new List<(string Path, DateTime Written)>();
        foreach (var candidate in candidates)
        {
            Guard(() =>
            {
                var info = new FileInfo(candidate);
                if (info.Exists) existing.Add((candidate, info.LastWriteTimeUtc));
            });
        }
        return existing.OrderByDescending(e => e.Written).Select(e => e.Path).ToList();
    }

    private static bool IsValidPath(string path)
    {
        try { _ = Path.GetFullPath(path); return true; }
        catch (Exception e) when (IsIo(e)) { return false; }
    }

    private static void Guard(Action probe)
    {
        try { probe(); }
        catch (Exception e) when (IsIo(e)) { /* this candidate is dropped, the others survive */ }
    }

    // PathTooLongException derives from IOException; SecurityException covers ACL'd package dirs.
    private static bool IsIo(Exception e) => e is IOException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or System.Security.SecurityException;
}
