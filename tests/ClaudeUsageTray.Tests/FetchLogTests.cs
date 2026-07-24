using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class FetchLogTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Directory.CreateTempSubdirectory("cut-log-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Path(string name = "fetch.log") => System.IO.Path.Combine(_dir, name);

    [Fact]
    public void Write_AppendsUtcTimestampedLine()
    {
        var path = Path();
        new FetchLog(path).Write(T0, "429 rate-limited");
        var text = File.ReadAllText(path);
        Assert.Equal($"2026-07-24T12:00:00Z 429 rate-limited{Environment.NewLine}", text);
    }

    [Fact]
    public void Write_ConvertsNonUtcOffsetToUtc()
    {
        var path = Path();
        new FetchLog(path).Write(new DateTimeOffset(2026, 7, 24, 14, 0, 0, TimeSpan.FromHours(2)), "ok");
        Assert.StartsWith("2026-07-24T12:00:00Z ok", File.ReadAllText(path));
    }

    [Fact]
    public void Write_AppendsAcrossCalls()
    {
        var path = Path();
        var log = new FetchLog(path);
        log.Write(T0, "attempt");
        log.Write(T0.AddSeconds(1), "200 ok");
        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("attempt", lines[0]);
        Assert.EndsWith("200 ok", lines[1]);
    }

    [Fact]
    public void Write_CreatesMissingDirectory()
    {
        var path = System.IO.Path.Combine(_dir, "nested", "sub", "fetch.log");
        new FetchLog(path).Write(T0, "ok");
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Write_RotatesToBackup_WhenOverCap()
    {
        var path = Path();
        // Seed a file just over the 256 KiB cap so the next write triggers rotation.
        File.WriteAllText(path, new string('x', 256 * 1024 + 1));
        new FetchLog(path).Write(T0, "after-rotate");

        Assert.True(File.Exists(path + ".1"));                      // old content preserved
        Assert.Equal($"2026-07-24T12:00:00Z after-rotate{Environment.NewLine}", File.ReadAllText(path));
    }

    [Fact]
    public void Write_RotationReplacesExistingBackup()
    {
        var path = Path();
        File.WriteAllText(path + ".1", "old-backup");
        File.WriteAllText(path, new string('x', 256 * 1024 + 1));
        new FetchLog(path).Write(T0, "new");
        Assert.DoesNotContain("old-backup", File.ReadAllText(path + ".1"));
    }

    [Fact]
    public void Write_NeverThrows_OnInvalidPath()
    {
        // A path whose "directory" is actually a file cannot be created — must be swallowed.
        var filePath = Path("blocker");
        File.WriteAllText(filePath, "x");
        var log = new FetchLog(System.IO.Path.Combine(filePath, "fetch.log"));
        log.Write(T0, "should not throw"); // no assertion needed: absence of exception is the test
    }
}
