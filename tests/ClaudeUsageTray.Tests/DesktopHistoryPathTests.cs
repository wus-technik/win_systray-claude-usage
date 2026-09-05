using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class DesktopHistoryPathTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cut-desktop-path-").FullName;
    private string AppData => Path.Combine(_dir, "Roaming");
    private string LocalAppData => Path.Combine(_dir, "Local");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Classic => Path.Combine(AppData, "Claude", DesktopHistoryPath.FileName);

    private string Container(string package = "Claude_pzs8sxrjxfjjc") => Path.Combine(
        LocalAppData, "Packages", package, "LocalCache", "Roaming", "Claude", DesktopHistoryPath.FileName);

    private static string Write(string path, DateTime writtenUtc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}");
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }

    private static readonly DateTime T0 = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

    // ---- Candidates ----

    [Fact]
    public void Candidates_ClassicPathAlwaysListed_EvenWhenNothingExists()
    {
        var c = DesktopHistoryPath.Candidates(null, AppData, LocalAppData);
        Assert.Equal([Classic], c);
    }

    [Fact]
    public void Candidates_IncludesEveryClaudePackageContainer()
    {
        Directory.CreateDirectory(Path.Combine(LocalAppData, "Packages", "Claude_aaaa"));
        Directory.CreateDirectory(Path.Combine(LocalAppData, "Packages", "Claude_bbbb"));
        Directory.CreateDirectory(Path.Combine(LocalAppData, "Packages", "Other_cccc"));

        var c = DesktopHistoryPath.Candidates(null, AppData, LocalAppData);

        Assert.Equal(3, c.Count);
        Assert.Contains(Classic, c);
        Assert.Contains(Container("Claude_aaaa"), c);
        Assert.Contains(Container("Claude_bbbb"), c);
    }

    [Fact]
    public void Candidates_Override_IsTheOnlyCandidate()
    {
        Write(Classic, T0);
        var overridePath = Path.Combine(_dir, "elsewhere.json");
        Assert.Equal([overridePath], DesktopHistoryPath.Candidates(overridePath, AppData, LocalAppData));
    }

    [Fact]
    public void Candidates_BlankOverride_IsIgnored()
        => Assert.Equal([Classic], DesktopHistoryPath.Candidates("   ", AppData, LocalAppData));

    [Fact]
    public void Candidates_InvalidOverride_IsEmptyAndDoesNotThrow()
        => Assert.Empty(DesktopHistoryPath.Candidates("C:\\bad\0path.json", AppData, LocalAppData));

    // ---- ByFreshness ----

    [Fact]
    public void ByFreshness_DropsMissingFiles()
    {
        Write(Classic, T0);
        var ordered = DesktopHistoryPath.ByFreshness([Classic, Container()]);
        Assert.Equal([Classic], ordered);
    }

    [Fact]
    public void ByFreshness_NoneExist_IsEmpty()
        => Assert.Empty(DesktopHistoryPath.ByFreshness([Classic, Container()]));

    [Fact]
    public void ByFreshness_NewerFileFirst_ClassicNewer()
    {
        Write(Classic, T0);
        Write(Container(), T0.AddHours(-1));
        Assert.Equal([Classic, Container()], DesktopHistoryPath.ByFreshness([Classic, Container()]));
    }

    [Fact]
    public void ByFreshness_NewerFileFirst_ContainerNewer()
    {
        Write(Classic, T0.AddDays(-30));
        Write(Container(), T0);
        Assert.Equal([Container(), Classic], DesktopHistoryPath.ByFreshness([Classic, Container()]));
    }

    /// <summary>An orphaned %APPDATA%\Claude keeps getting touched while its usage file is weeks old;
    /// the ordering must read the file, not its directory.</summary>
    [Fact]
    public void ByFreshness_IgnoresDirectoryWriteTime()
    {
        Write(Classic, T0.AddDays(-30));
        Write(Container(), T0.AddHours(-1));
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(Classic)!, T0);

        Assert.Equal([Container(), Classic], DesktopHistoryPath.ByFreshness([Classic, Container()]));
    }

    [Fact]
    public void ByFreshness_InvalidCandidate_IsDroppedNotThrown()
    {
        Write(Classic, T0);
        Assert.Equal([Classic], DesktopHistoryPath.ByFreshness(["C:\\bad\0path.json", Classic]));
    }
}
