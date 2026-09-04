using ClaudeUsageTraySetupStub;
using Xunit;

namespace ClaudeUsageTraySetupStub.Tests;

public class InstallStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stub-root-" + Guid.NewGuid().ToString("N"));
    public InstallStateTests() => Directory.CreateDirectory(Path.Combine(_root, "current"));
    public void Dispose() => Directory.Delete(_root, recursive: true);

    // The real file vpk writes: a nuspec with a default namespace, which XPath-by-name would miss.
    private const string RealManifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd">
        <metadata>
        <id>WusTechnik.ClaudeUsageTray</id>
        <title>Claude Usage Tray</title>
        <version>0.7.2</version>
        <channel>win-beta</channel>
        <mainExe>ClaudeUsageTray.exe</mainExe>
        <releaseNotes><![CDATA[ notes ]]></releaseNotes>
        </metadata>
        </package>
        """;

    [Fact]
    public void ParsesVersionAndChannelFromTheNamespacedNuspec()
        => Assert.Equal(new InstallManifest("0.7.2", "win-beta"), SqVersion.Parse(RealManifest));

    [Fact]
    public void MissingChannelIsNullNotStable()
    {
        // Older manifests may lack it; the caller decides what null means.
        var xml = RealManifest.Replace("<channel>win-beta</channel>", "");
        Assert.Equal(new InstallManifest("0.7.2", null), SqVersion.Parse(xml));
    }

    [Theory]
    [InlineData("<package><metadata></metadata></package>")]
    [InlineData("<package><metadata><version> </version></metadata></package>")]
    [InlineData("not xml at all")]
    [InlineData("")]
    public void UnusableManifestsParseToNull(string xml) => Assert.Null(SqVersion.Parse(xml));

    [Fact]
    public void DetectReadsTheManifestFirst()
    {
        File.WriteAllText(InstallPaths.Manifest(_root), RealManifest);
        var info = InstallDetection.Detect(_root, () => throw new Xunit.Sdk.XunitException("registry must not be consulted"));
        Assert.Equal(new InstallInfo("0.7.2", "win-beta"), info);
    }

    [Fact]
    public void DetectFallsBackToTheUninstallKeyWhenTheManifestIsMissing()
    {
        // A wrong "not installed" would run Setup.exe, which silently no-ops on an existing install.
        var info = InstallDetection.Detect(_root, () => "0.7.1");
        Assert.Equal(new InstallInfo("0.7.1", null), info);
    }

    [Fact]
    public void DetectFallsBackWhenTheManifestIsMalformed()
    {
        File.WriteAllText(InstallPaths.Manifest(_root), "<<<");
        Assert.Equal(new InstallInfo("0.7.1", null), InstallDetection.Detect(_root, () => "0.7.1"));
    }

    [Fact]
    public void NothingAnywhereMeansNotInstalled()
        => Assert.Null(InstallDetection.Detect(_root, () => null));

    [Fact]
    public void PathsAreUnderTheRoot()
    {
        Assert.Equal(Path.Combine(_root, "current", "ClaudeUsageTray.exe"), InstallPaths.CurrentExe(_root));
        Assert.Equal(Path.Combine(_root, "current", "sq.version"), InstallPaths.Manifest(_root));
        Assert.Equal(Path.Combine(_root, "Update.exe"), InstallPaths.UpdateExe(_root));
        Assert.EndsWith(@"\WusTechnik.ClaudeUsageTray", InstallPaths.DefaultRoot);
    }

    // ---- current ring: explicit setting wins, otherwise the manifest channel ----

    [Theory]
    [InlineData(null, "win-beta", Ring.Beta)]   // null in the file is a normal state for a beta install
    [InlineData(null, "win", Ring.Stable)]
    [InlineData(null, null, Ring.Stable)]
    [InlineData(false, "win-beta", Ring.Stable)] // explicit opt-out beats the channel
    [InlineData(true, "win", Ring.Beta)]
    [InlineData(true, null, Ring.Beta)]
    public void CurrentRingResolution(bool? useBetaReleases, string? channel, Ring expected)
        => Assert.Equal(expected, CurrentRing.Resolve(useBetaReleases, channel));
}
