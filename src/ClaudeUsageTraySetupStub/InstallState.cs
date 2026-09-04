using System.Security;
using System.Xml;
using System.Xml.Linq;
using ClaudeUsageTray.Core;
using Microsoft.Win32;

namespace ClaudeUsageTraySetupStub;

/// <summary>Where a per-user Velopack install of the app lives. Fixed on purpose: nothing in the app
/// supports a relocated install, which is why the stub does not expose Setup.exe's --installto.</summary>
public static class InstallPaths
{
    public const string PackId = "WusTechnik.ClaudeUsageTray";
    /// <summary>Program.cs acquires this at launch (SingleInstance.cs). The reliable "is the tray
    /// running in this session" probe — and it distinguishes the installed copy from a portable one
    /// only together with the process path.</summary>
    public const string MutexName = @"Local\WusTechnik.ClaudeUsageTray";
    public const string ExeName = "ClaudeUsageTray";

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), PackId);

    public static string CurrentExe(string root) => Path.Combine(root, "current", ExeName + ".exe");
    public static string Manifest(string root) => Path.Combine(root, "current", "sq.version");
    public static string UpdateExe(string root) => Path.Combine(root, "Update.exe");
}

public sealed record InstallManifest(string Version, string? Channel);

/// <summary>`current/sq.version` is the package nuspec — plain XML with a default namespace, so
/// elements are matched by local name. Reading it needs no Velopack dependency.</summary>
public static class SqVersion
{
    public static InstallManifest? Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var metadata = XDocument.Parse(xml).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
            var version = metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == "version")?.Value.Trim();
            if (string.IsNullOrEmpty(version)) return null;
            var channel = metadata!.Elements().FirstOrDefault(e => e.Name.LocalName == "channel")?.Value.Trim();
            return new InstallManifest(version, string.IsNullOrEmpty(channel) ? null : channel);
        }
        catch (XmlException)
        {
            return null;
        }
    }
}

public sealed record InstallInfo(string Version, string? Channel);

public static class InstallDetection
{
    /// <summary>Manifest first; a missing or malformed one falls back to the HKCU uninstall key before
    /// concluding "not installed". Concluding it wrongly would run Setup.exe, which silently no-ops on
    /// an existing install — the stub would then report success having changed nothing.</summary>
    public static InstallInfo? Detect(string root, Func<string?> uninstallKeyVersion)
    {
        var manifestPath = InstallPaths.Manifest(root);
        if (File.Exists(manifestPath))
        {
            try
            {
                if (SqVersion.Parse(File.ReadAllText(manifestPath)) is { } manifest)
                    return new InstallInfo(manifest.Version, manifest.Channel);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        var fromRegistry = uninstallKeyVersion();
        return fromRegistry is null ? null : new InstallInfo(fromRegistry, null);
    }

    /// <summary>Velopack registers the per-user uninstall entry under HKCU, keyed by the pack id. The
    /// registry knows the version but not the channel.</summary>
    public static string? ReadUninstallKeyVersion()
    {
        try
        {
            using var uninstall = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null) return null;
            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var key = uninstall.OpenSubKey(name);
                if (key is null) continue;
                var location = (key.GetValue("InstallLocation") as string)?.TrimEnd('\\');
                if (string.Equals(name, InstallPaths.PackId, StringComparison.OrdinalIgnoreCase)
                    || (location is not null && location.EndsWith(InstallPaths.PackId, StringComparison.OrdinalIgnoreCase)))
                    return key.GetValue("DisplayVersion") as string ?? "unknown";
            }
        }
        catch (Exception e) when (e is SecurityException or IOException or UnauthorizedAccessException) { }
        return null;
    }
}

/// <summary>Which ring an existing install is on. An explicit setting wins; otherwise the manifest
/// channel decides — the same adoption rule Program.cs applies at launch, and the reason a null
/// setting on a win-beta install must read as beta, not stable.</summary>
public static class CurrentRing
{
    public static Ring Resolve(bool? useBetaReleases, string? manifestChannel)
        => (useBetaReleases ?? UpdateRing.IsBetaChannel(manifestChannel)) ? Ring.Beta : Ring.Stable;
}
